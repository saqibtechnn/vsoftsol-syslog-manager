using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Drains the <c>rule_action_queue</c> outbox and executes each action <b>off the ingest
/// thread</b> (PHASE_07 — "Actions must execute off the ingest thread; a slow action does
/// not stall ingestion"). Claims rows in batches, executes up to
/// <see cref="ActionDispatchOptions.MaxParallelism"/> at once, retries transient failures
/// with exponential back-off, dead-letters after
/// <see cref="ActionDispatchOptions.MaxAttempts"/>, and audits every outcome. Also flushes
/// rule hit counters and purges completed rows.
/// </summary>
public sealed class ActionDispatchService : BackgroundService
{
    private readonly SqliteActionOutbox _outbox;
    private readonly ILogRepository _repository;
    private readonly SqliteAuditLog _audit;
    private readonly SqliteRuleStore _rules;
    private readonly RuleHitTracker _hits;
    private readonly ActionExecutorRegistry _executors;
    private readonly SecretResolver _secrets;
    private readonly NotificationSink _notifications;
    private readonly ActionExecutorOptions _actionOptions;
    private readonly ActionDispatchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ActionDispatchService> _logger;

    private DateTimeOffset _lastHitFlush;
    private DateTimeOffset _lastPurge;

    public ActionDispatchService(
        SqliteActionOutbox outbox,
        ILogRepository repository,
        SqliteAuditLog audit,
        SqliteRuleStore rules,
        RuleHitTracker hits,
        ActionExecutorRegistry executors,
        SecretResolver secrets,
        NotificationSink notifications,
        IOptions<ActionExecutorOptions> actionOptions,
        IOptions<ActionDispatchOptions> options,
        TimeProvider time,
        ILogger<ActionDispatchService> logger)
    {
        _outbox = outbox;
        _repository = repository;
        _audit = audit;
        _rules = rules;
        _hits = hits;
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
        _logger.LogInformation(
            "Action dispatcher started (poll {Poll}, batch {Batch}, parallelism {P}).",
            _options.PollInterval, _options.ClaimBatchSize, _options.MaxParallelism);

        // On start, anything left 'running' is from a previous process that crashed mid-action.
        try
        {
            int recovered = await _outbox
                .RecoverStaleRunningAsync(_time.GetUtcNow() - _options.StaleRunningAfter, stoppingToken)
                .ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogWarning("Recovered {Count} stale 'running' action(s) from a previous run.", recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Stale-action recovery failed on start.");
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
                _logger.LogError(ex, "Action dispatch pass failed; retrying after the poll interval.");
                handled = 0;
            }

            if (handled >= _options.ClaimBatchSize)
            {
                continue; // batch was full — keep draining
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await FlushHitsAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Action dispatcher stopped.");
    }

    private async Task<int> PassAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueuedAction> batch =
            await _outbox.ClaimBatchAsync(_options.ClaimBatchSize, cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return 0;
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxParallelism));
        var tasks = new List<Task>(batch.Count);
        foreach (QueuedAction queued in batch)
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

    private async Task RunOneAsync(QueuedAction queued, CancellationToken cancellationToken)
    {
        RuleAction? action = RuleJson.DeserializeAction(queued.PayloadJson);
        if (action is null)
        {
            await _outbox.DeadLetterAsync(queued.QueueId, "payload could not be deserialised", cancellationToken)
                .ConfigureAwait(false);
            await AuditAsync(AuditActions.ActionDeadLettered, queued, "unparseable payload", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        SyslogEvent? evt = await _repository.GetByIdAsync(queued.EventId, cancellationToken).ConfigureAwait(false);
        if (evt is null)
        {
            // The event was purged (retention) before the action ran — nothing to do.
            await _outbox.CompleteAsync(queued.QueueId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var context = new ActionContext(
            queued.RuleId, RuleName(queued), action, evt, _secrets, _notifications, _actionOptions);

        ActionResult result;
        try
        {
            result = await _executors.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down — leave the row 'running'; RecoverStaleRunningAsync re-queues it.
            return;
        }

        if (result.Ok)
        {
            await _outbox.CompleteAsync(queued.QueueId, cancellationToken).ConfigureAwait(false);
            await AuditAsync(AuditActions.ActionFired, queued, result.Detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.Retryable && queued.Attempts < _options.MaxAttempts)
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
            $"Rule action failed permanently",
            $"'{RuleActionInfo.Label(action)}' for rule '{RuleName(queued)}' was dead-lettered: {result.Detail}",
            queued.RuleId == 0 ? null : queued.RuleId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MaintenanceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (now - _lastHitFlush >= _options.HitFlushInterval)
        {
            await FlushHitsAsync(cancellationToken).ConfigureAwait(false);
            _lastHitFlush = now;
        }

        if (now - _lastPurge >= TimeSpan.FromHours(1))
        {
            try
            {
                await _outbox.PurgeCompletedAsync(now - _options.PurgeCompletedAfter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Outbox purge failed.");
            }

            _lastPurge = now;
        }
    }

    private async Task FlushHitsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<long, (long Delta, DateTimeOffset LastFired)> pending = _hits.Drain();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await _rules.BumpHitsAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Rule hit-count flush failed; counts will re-accumulate.");
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        double seconds = _options.BackoffBase.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromMinutes(30).TotalSeconds));
    }

    private static string RuleName(QueuedAction queued) => queued.RuleId == 0 ? "system" : $"#{queued.RuleId}";

    private Task AuditAsync(string action, QueuedAction queued, string detail, CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditEntry(
                action,
                Actor: "rules-engine",
                EntityType: "rule",
                EntityId: queued.RuleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Detail: $"{queued.Kind} on event {queued.EventId}: {Trim(detail)}"),
            CancellationToken.None);

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
