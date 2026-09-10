using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Drains the <c>alert_action_queue</c> outbox and runs each action off the scheduler thread
/// (ADR 0016) — the same claim → run → complete / back-off / dead-letter contract as the
/// Phase 7 <see cref="ActionDispatchService"/>. Because an alert has no single triggering
/// message, the executor is handed a <b>synthetic</b> <see cref="SyslogEvent"/> built from
/// the firing instance so <c>{hostname}</c> / <c>{message}</c> / <c>{severity}</c> /
/// <c>{field.alert_*}</c> templates render.
/// </summary>
public sealed class AlertActionDispatchService : BackgroundService
{
    private readonly SqliteAlertActionOutbox _outbox;
    private readonly SqliteAlertStore _alerts;
    private readonly SqliteAlertInstanceStore _instances;
    private readonly SqliteAuditLog _audit;
    private readonly ActionExecutorRegistry _executors;
    private readonly SecretResolver _secrets;
    private readonly NotificationSink _notifications;
    private readonly ActionExecutorOptions _actionOptions;
    private readonly AlertEvaluationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertActionDispatchService> _logger;

    private DateTimeOffset _lastPurge;

    public AlertActionDispatchService(
        SqliteAlertActionOutbox outbox,
        SqliteAlertStore alerts,
        SqliteAlertInstanceStore instances,
        SqliteAuditLog audit,
        ActionExecutorRegistry executors,
        SecretResolver secrets,
        NotificationSink notifications,
        IOptions<ActionExecutorOptions> actionOptions,
        IOptions<AlertEvaluationOptions> options,
        TimeProvider time,
        ILogger<AlertActionDispatchService> logger)
    {
        _outbox = outbox;
        _alerts = alerts;
        _instances = instances;
        _audit = audit;
        _executors = executors;
        _secrets = secrets;
        _notifications = notifications;
        _actionOptions = actionOptions.Value;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alert action dispatcher started.");

        try
        {
            int recovered = await _outbox
                .RecoverStaleRunningAsync(_time.GetUtcNow() - _options.StaleRunningAfter, stoppingToken)
                .ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogWarning("Recovered {Count} stale alert action(s) from a previous run.", recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Stale alert-action recovery failed on start.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int handled;
            try
            {
                handled = await PassAsync(stoppingToken).ConfigureAwait(false);
                await MaintenanceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert action dispatch pass failed.");
                handled = 0;
            }

            if (handled >= _options.DispatchBatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(_options.DispatchPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Alert action dispatcher stopped.");
    }

    /// <summary>One drain pass. Public so tests can drive it directly.</summary>
    public async Task<int> PassAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueuedAlertAction> batch =
            await _outbox.ClaimBatchAsync(_options.DispatchBatchSize, cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return 0;
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.DispatchParallelism));
        var tasks = new List<Task>(batch.Count);
        foreach (QueuedAlertAction queued in batch)
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await RunOneAsync(queued, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return batch.Count;
    }

    private async Task RunOneAsync(QueuedAlertAction queued, CancellationToken cancellationToken)
    {
        RuleAction? action = AlertJson.DeserializeAction(queued.PayloadJson);
        if (action is null)
        {
            await _outbox.DeadLetterAsync(queued.QueueId, "payload could not be deserialised", cancellationToken).ConfigureAwait(false);
            await AuditAsync(AuditActions.ActionDeadLettered, queued, "unparseable payload", cancellationToken).ConfigureAwait(false);
            return;
        }

        AlertRow? alert = await _alerts.GetAsync(queued.AlertId, cancellationToken).ConfigureAwait(false);
        AlertInstance? instance = await _instances.GetAsync(queued.InstanceId, cancellationToken).ConfigureAwait(false);
        if (alert is null || instance is null)
        {
            await _outbox.CompleteAsync(queued.QueueId, cancellationToken).ConfigureAwait(false);
            return;
        }

        SyslogEvent synthetic = BuildSyntheticEvent(alert, instance);
        var context = new ActionContext(queued.AlertId, alert.Name, action, synthetic, _secrets, _notifications, _actionOptions);

        ActionResult result;
        try
        {
            result = await _executors.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // shutting down — leave 'running'; recovery re-queues it
        }

        if (result.Ok)
        {
            await _outbox.CompleteAsync(queued.QueueId, cancellationToken).ConfigureAwait(false);
            await AuditAsync(AuditActions.ActionFired, queued, result.Detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.Retryable && queued.Attempts < _options.DispatchMaxAttempts)
        {
            DateTimeOffset next = _time.GetUtcNow() + Backoff(queued.Attempts);
            await _outbox.FailAsync(queued.QueueId, next, result.Detail, cancellationToken).ConfigureAwait(false);
            await AuditAsync(AuditActions.ActionFailed, queued,
                $"attempt {queued.Attempts} failed ({result.Detail}); retry at {next:O}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _outbox.DeadLetterAsync(queued.QueueId, result.Detail, cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.ActionDeadLettered, queued,
            $"gave up after {queued.Attempts} attempt(s): {result.Detail}", cancellationToken).ConfigureAwait(false);
        await _notifications(
            NotificationLevel.Warning,
            "Alert action failed permanently",
            $"'{RuleActionInfo.Label(action)}' for alert '{alert.Name}' was dead-lettered: {result.Detail}",
            queued.AlertId,
            cancellationToken).ConfigureAwait(false);
    }

    private static SyslogEvent BuildSyntheticEvent(AlertRow alert, AlertInstance instance)
    {
        string group = instance.GroupValue ?? "(all)";
        string summary =
            $"Alert '{alert.Name}' fired for {group}: observed {instance.ObservedValue} (threshold {instance.Threshold}).";

        var fields = new List<EventField>
        {
            new("alert_name", alert.Name),
            new("alert_group", group),
            new("alert_value", instance.ObservedValue.ToString(CultureInfo.InvariantCulture)),
            new("alert_threshold", instance.Threshold.ToString(CultureInfo.InvariantCulture)),
            new("alert_state", instance.State.ToString().ToLowerInvariant()),
            new("alert_severity", alert.Severity.ToString().ToLowerInvariant()),
        };
        if (!string.IsNullOrWhiteSpace(alert.RemediationNotes))
        {
            fields.Add(new EventField("alert_remediation", alert.RemediationNotes));
        }

        return new SyslogEvent
        {
            EventId = 0,
            ReceivedUtc = instance.OpenedUtc,
            EventUtc = instance.OpenedUtc,
            SourceIp = LooksLikeIp(instance.GroupValue) ? instance.GroupValue! : "0.0.0.0",
            Hostname = instance.GroupValue,
            AppName = "alerts",
            Facility = Facility.Local0,
            Severity = alert.Severity switch
            {
                NotificationLevel.Critical => Severity.Critical,
                NotificationLevel.Info => Severity.Informational,
                _ => Severity.Warning,
            },
            Protocol = Protocol.Udp,
            Message = summary,
            RawMessage = Encoding.UTF8.GetBytes(summary),
            ParseStatus = ParseStatus.Rfc5424,
            Fields = fields,
        };
    }

    private static bool LooksLikeIp(string? value) =>
        value is not null && System.Net.IPAddress.TryParse(value, out _);

    private async Task MaintenanceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now - _lastPurge < TimeSpan.FromHours(1))
        {
            return;
        }

        try
        {
            await _outbox.PurgeCompletedAsync(now - _options.PurgeCompletedAfter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Alert outbox purge failed.");
        }

        _lastPurge = now;
    }

    private TimeSpan Backoff(int attempt)
    {
        double seconds = _options.DispatchBackoffBase.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromMinutes(30).TotalSeconds));
    }

    private Task AuditAsync(string action, QueuedAlertAction queued, string detail, CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditEntry(
                action,
                Actor: "alerts-engine",
                EntityType: "alert",
                EntityId: queued.AlertId.ToString(CultureInfo.InvariantCulture),
                Detail: $"{queued.Kind} for instance {queued.InstanceId}: {Trim(detail)}"),
            CancellationToken.None);

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
