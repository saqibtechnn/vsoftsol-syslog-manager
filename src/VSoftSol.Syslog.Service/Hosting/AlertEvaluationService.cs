using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Rules.Alerts;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The alert scheduler (PHASE_08 build item 3; ADR 0016). Runs off the ingest path in the
/// collector host only. Each tick: for every enabled alert that is due (per its interval and
/// the persisted <c>alert_eval_runs</c> checkpoint), fetch the window data (SQL aggregate,
/// or an in-memory filtered scan when the alert has a <c>ConditionGroup</c>), evaluate it,
/// then reconcile <c>alert_instances</c> — open a new instance on a fresh breach (dedup on
/// the store's partial unique index), re-notify an open instance only on its re-notify
/// interval, and auto-resolve when the condition clears. A run overdue by more than the
/// grace window (the process was down) is logged, not silently skipped.
/// </summary>
public sealed class AlertEvaluationService : BackgroundService
{
    private readonly AlertSetProvider _alerts;
    private readonly SqliteAlertStore _store;
    private readonly SqliteAlertInstanceStore _instances;
    private readonly SqliteAlertWindowReader _reader;
    private readonly SqliteAlertActionOutbox _outbox;
    private readonly SqliteAuditLog _audit;
    private readonly AlertRuntime _runtime;
    private readonly NotificationSink _notifications;
    private readonly AlertEvaluationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertEvaluationService> _logger;

    public AlertEvaluationService(
        AlertSetProvider alerts,
        SqliteAlertStore store,
        SqliteAlertInstanceStore instances,
        SqliteAlertWindowReader reader,
        SqliteAlertActionOutbox outbox,
        SqliteAuditLog audit,
        AlertRuntime runtime,
        NotificationSink notifications,
        IOptions<AlertEvaluationOptions> options,
        TimeProvider time,
        ILogger<AlertEvaluationService> logger)
    {
        _alerts = alerts;
        _store = store;
        _instances = instances;
        _reader = reader;
        _outbox = outbox;
        _audit = audit;
        _runtime = runtime;
        _notifications = notifications;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alert scheduler started (tick {Tick}).", _options.TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert evaluation tick failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_options.TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Alert scheduler stopped.");
    }

    /// <summary>One scheduler pass. Public so tests can drive it on a virtual clock.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        CompiledAlertSet set = await _alerts.GetAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();

        var due = new List<CompiledAlert>();
        foreach (CompiledAlert alert in set.Alerts)
        {
            AlertEvalRun? run = await _store.GetEvalRunAsync(alert.AlertId, cancellationToken).ConfigureAwait(false);
            if (run?.LastRunUtc is { } last)
            {
                TimeSpan since = now - last;
                if (since < TimeSpan.FromSeconds(alert.IntervalSeconds))
                {
                    continue;
                }

                if (since > TimeSpan.FromSeconds((double)alert.IntervalSeconds * _options.MissedRunGraceMultiplier))
                {
                    _logger.LogWarning(
                        "Alert '{Name}' (#{Id}) missed one or more evaluations ({Since} since the last run); catching up now.",
                        alert.Name, alert.AlertId, since);
                    await AuditAsync(AuditActions.AlertEvaluationMissed, alert.AlertId, alert.Name,
                        $"{since.TotalSeconds:F0}s since the previous run (interval {alert.IntervalSeconds}s)", cancellationToken).ConfigureAwait(false);
                }
            }

            due.Add(alert);
        }

        if (due.Count > 0)
        {
            using var throttle = new SemaphoreSlim(Math.Max(1, _options.DispatchParallelism));
            IEnumerable<Task> tasks = due.Select(async alert =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EvaluateOneAsync(alert, now, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        if (_runtime.TakeStormSummary() is { } storm)
        {
            await _notifications(
                NotificationLevel.Warning,
                "Alert-storm protection engaged",
                $"{storm.TotalCollapsed} alert action(s) suppressed in the last minute: " +
                string.Join(", ", storm.ByKind.Select(kv => $"{kv.Value}× {kv.Key}")),
                null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateOneAsync(CompiledAlert alert, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset windowStart = now - TimeSpan.FromSeconds(alert.WindowSeconds);
        try
        {
            AlertWindowData data = await FetchWindowAsync(alert, windowStart, now, cancellationToken).ConfigureAwait(false);
            AlertEvaluation evaluation = AlertEvaluator.Evaluate(alert, data, now);
            await ReconcileAsync(alert, evaluation, now, data.Truncated, cancellationToken).ConfigureAwait(false);

            await _store.RecordEvaluatedAsync(alert.AlertId, now, cancellationToken).ConfigureAwait(false);
            await _store.RecordEvalRunAsync(alert.AlertId, now, now, "ok", failed: false, cancellationToken).ConfigureAwait(false);

            if (data.Truncated)
            {
                await _notifications(
                    NotificationLevel.Info,
                    $"Alert '{alert.Name}' hit its scan cap",
                    "The evaluation window has more matching events than the scan limit; the count is a lower bound.",
                    alert.AlertId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Alert '{Name}' (#{Id}) evaluation failed.", alert.Name, alert.AlertId);
            await _store.RecordEvalRunAsync(alert.AlertId, now, now, "error: " + Trim(ex.Message), failed: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AlertWindowData> FetchWindowAsync(
        CompiledAlert alert, DateTimeOffset windowStart, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<long> deviceIds = alert.DeviceGroupIds.Count > 0
            ? await _reader.ResolveDeviceIdsAsync(alert.DeviceGroupIds, cancellationToken).ConfigureAwait(false)
            : [];

        switch (alert.Type)
        {
            case AlertEvaluationType.DeviceSilent:
                {
                    IReadOnlyList<DeviceSilence> silences = await _reader
                        .DeviceLastSeenAsync(alert.DeviceGroupIds, Math.Max(1, alert.WindowSeconds / 60), cancellationToken)
                        .ConfigureAwait(false);
                    return new AlertWindowData { DeviceSilences = silences };
                }

            case AlertEvaluationType.DistinctCount:
                {
                    if (alert.AlwaysMatches && SqliteAlertWindowReader.IsSqlGroupable(alert.GroupByField))
                    {
                        (long distinct, IReadOnlyList<long> sample) = await _reader
                            .DistinctCountAsync(windowStart, now, alert.GroupByField!, deviceIds, alert.StreamIds, _options.TriggerEventSample, cancellationToken)
                            .ConfigureAwait(false);
                        return new AlertWindowData { DistinctValueCount = distinct, DistinctSampleEventIds = sample };
                    }

                    return await InMemoryAsync(alert, windowStart, now, deviceIds, distinct: true, cancellationToken).ConfigureAwait(false);
                }

            default: // Threshold, Absence
                {
                    bool sqlGroupable = alert.GroupByField is null || SqliteAlertWindowReader.IsSqlGroupable(alert.GroupByField);
                    if (alert.AlwaysMatches && sqlGroupable)
                    {
                        IReadOnlyList<GroupCount> counts = await _reader
                            .CountByGroupAsync(windowStart, now, alert.GroupByField, deviceIds, alert.StreamIds, _options.TriggerEventSample, cancellationToken)
                            .ConfigureAwait(false);
                        return new AlertWindowData { GroupCounts = counts };
                    }

                    return await InMemoryAsync(alert, windowStart, now, deviceIds, distinct: false, cancellationToken).ConfigureAwait(false);
                }
        }
    }

    private async Task<AlertWindowData> InMemoryAsync(
        CompiledAlert alert, DateTimeOffset windowStart, DateTimeOffset now,
        IReadOnlyList<long> deviceIds, bool distinct, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var distinctValues = new HashSet<string>(StringComparer.Ordinal);
        var distinctSample = new List<long>();
        int scanned = 0;
        bool truncated = false;

        await foreach (SyslogEvent evt in _reader.StreamWindowAsync(
            windowStart, now, deviceIds, alert.StreamIds, _options.MaxWindowScan, cancellationToken).ConfigureAwait(false))
        {
            if (++scanned > _options.MaxWindowScan)
            {
                truncated = true;
                break;
            }

            if (!alert.FilterMatches(evt))
            {
                continue;
            }

            if (distinct)
            {
                string? key = AlertGrouping.DistinctKeyFor(alert.GroupByField!, evt);
                if (key is not null && distinctValues.Add(key) && distinctSample.Count < _options.TriggerEventSample)
                {
                    distinctSample.Add(evt.EventId);
                }
            }
            else
            {
                string group = AlertGrouping.KeyFor(alert.GroupByField, evt) ?? SqliteAlertInstanceStore.UngroupedKey;
                counts[group] = counts.GetValueOrDefault(group) + 1;
                List<long> bucket = samples.TryGetValue(group, out List<long>? list) ? list : samples[group] = [];
                if (bucket.Count < _options.TriggerEventSample)
                {
                    bucket.Add(evt.EventId);
                }
            }
        }

        if (distinct)
        {
            return new AlertWindowData
            {
                DistinctValueCount = distinctValues.Count,
                DistinctSampleEventIds = distinctSample,
                Truncated = truncated,
            };
        }

        // Absence needs a row even when nothing matched.
        if (alert.Type == AlertEvaluationType.Absence && counts.Count == 0)
        {
            return new AlertWindowData { GroupCounts = [new GroupCount(null, 0, [])], Truncated = truncated };
        }

        var groupCounts = counts
            .Select(kv => new GroupCount(
                alert.GroupByField is null ? null : kv.Key,
                kv.Value,
                samples.GetValueOrDefault(kv.Key, [])))
            .ToList();

        return new AlertWindowData { GroupCounts = groupCounts, Truncated = truncated };
    }

    private async Task ReconcileAsync(
        CompiledAlert alert, AlertEvaluation evaluation, DateTimeOffset now, bool truncated, CancellationToken cancellationToken)
    {
        var breachingGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (AlertBreach breach in evaluation.Breaches)
        {
            string groupKey = breach.GroupValue ?? SqliteAlertInstanceStore.UngroupedKey;
            breachingGroups.Add(groupKey);

            (long instanceId, bool created) = await _instances.OpenAsync(
                alert.AlertId, alert.Severity, breach.GroupValue, breach.ObservedValue, alert.Threshold,
                breach.TriggerEventIds, now, cancellationToken).ConfigureAwait(false);

            if (created)
            {
                await _store.RecordFiredAsync(alert.AlertId, now, cancellationToken).ConfigureAwait(false);
                await AuditAsync(AuditActions.AlertFired, alert.AlertId, alert.Name,
                    $"{Describe(breach)} (instance {instanceId})", cancellationToken).ConfigureAwait(false);
                await NotifyAsync(alert, instanceId, notifySeq: 0, breach, cancellationToken).ConfigureAwait(false);
                await _instances.MarkNotifiedAsync(instanceId, now, cancellationToken).ConfigureAwait(false);
            }
            else if (alert.ReNotifySeconds > 0)
            {
                AlertInstance? open = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
                if (open?.LastNotifiedUtc is { } lastNotified
                    && now - lastNotified >= TimeSpan.FromSeconds(alert.ReNotifySeconds))
                {
                    int seq = (int)Math.Max(1, (now - open.OpenedUtc).TotalSeconds / Math.Max(1, alert.ReNotifySeconds));
                    await NotifyAsync(alert, instanceId, seq, breach, cancellationToken).ConfigureAwait(false);
                    await _instances.MarkNotifiedAsync(instanceId, now, cancellationToken).ConfigureAwait(false);
                    await AuditAsync(AuditActions.AlertRenotified, alert.AlertId, alert.Name,
                        $"instance {instanceId} re-notified", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (!alert.AutoResolve)
        {
            return;
        }

        foreach (AlertInstance open in await _instances.ListForAlertAsync(alert.AlertId, 500, cancellationToken).ConfigureAwait(false))
        {
            if (!open.IsOpen)
            {
                continue;
            }

            string key = open.GroupValue ?? SqliteAlertInstanceStore.UngroupedKey;
            if (breachingGroups.Contains(key))
            {
                continue;
            }

            // The condition cleared for a group that was breaching — auto-resolve (but not
            // when the scan was truncated, since we cannot be sure it cleared).
            if (truncated)
            {
                continue;
            }

            if (await _instances.ResolveAsync(open.InstanceId, "alerts-engine", "condition cleared", auto: true, now, cancellationToken).ConfigureAwait(false))
            {
                await AuditAsync(AuditActions.AlertAutoResolved, alert.AlertId, alert.Name,
                    $"instance {open.InstanceId} ({key}) auto-resolved", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task NotifyAsync(
        CompiledAlert alert, long instanceId, int notifySeq, AlertBreach breach, CancellationToken cancellationToken)
    {
        if (alert.Actions.Count == 0)
        {
            return;
        }

        var allowed = new List<RuleAction>(alert.Actions.Count);
        foreach (RuleAction action in alert.Actions)
        {
            if (_runtime.Reserve(alert.AlertId, action) == AlertDispatchDecision.Allow)
            {
                allowed.Add(action);
            }
        }

        if (allowed.Count > 0)
        {
            await _outbox.EnqueueAsync(alert.AlertId, instanceId, notifySeq, allowed, cancellationToken).ConfigureAwait(false);
        }

        _ = breach;
    }

    private Task AuditAsync(string action, long alertId, string name, string detail, CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditEntry(
                action,
                Actor: "alerts-engine",
                EntityType: "alert",
                EntityId: alertId.ToString(CultureInfo.InvariantCulture),
                Detail: $"{name}: {Trim(detail)}"),
            CancellationToken.None);

    private static string Describe(AlertBreach breach) =>
        breach.GroupValue is null
            ? $"observed {breach.ObservedValue}"
            : $"'{breach.GroupValue}' observed {breach.ObservedValue}";

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
