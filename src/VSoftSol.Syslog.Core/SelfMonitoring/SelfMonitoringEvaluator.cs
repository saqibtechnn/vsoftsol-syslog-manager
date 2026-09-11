using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.SelfMonitoring;

/// <summary>
/// Pure breach/clear decision logic for the five self-monitoring conditions (PHASE_11 item
/// 6). "The application emits its own health events into a reserved internal stream"
/// (item 7) means this evaluator's job stops at deciding <em>whether</em> and <em>what</em>
/// to say — the caller (a collector-host <c>BackgroundService</c>) turns each
/// <see cref="SelfMonitoringTransition"/> into a real event through the normal repository,
/// so the existing Phase 7/8 rules and alerts engine is what actually notifies anyone; no
/// parallel alerting mechanism exists.
/// </summary>
public static class SelfMonitoringEvaluator
{
    public static (SelfMonitoringState NewState, IReadOnlyList<SelfMonitoringTransition> Transitions) Evaluate(
        SelfMonitoringSnapshot snapshot, SelfMonitoringThresholds thresholds, SelfMonitoringState previous)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(previous);

        var newMetrics = new Dictionary<SelfMonitoringMetric, SelfMonitoringMetricState>();
        var transitions = new List<SelfMonitoringTransition>();

        void Evaluate(SelfMonitoringMetric metric, bool conditionMet, bool requiresSustain, Func<Severity> severity, Func<string> message, Func<IReadOnlyDictionary<string, string>> fields)
        {
            previous.Metrics.TryGetValue(metric, out SelfMonitoringMetricState? prior);

            if (!conditionMet)
            {
                if (prior is { Notified: true })
                {
                    transitions.Add(new SelfMonitoringTransition(metric, SelfMonitoringTransitionKind.Cleared, Severity.Informational,
                        $"{metric} condition cleared.", new Dictionary<string, string> { ["metric"] = metric.ToString() }));
                }

                return; // no entry carried forward — the episode is over
            }

            DateTimeOffset since = prior?.BreachedSinceUtc ?? snapshot.ObservedUtc;
            bool sustainedEnough = !requiresSustain || snapshot.ObservedUtc - since >= thresholds.QueueDepthSustainedFor;
            bool alreadyNotified = prior?.Notified ?? false;

            if (sustainedEnough && !alreadyNotified)
            {
                transitions.Add(new SelfMonitoringTransition(metric, SelfMonitoringTransitionKind.Started, severity(), message(), fields()));
            }

            newMetrics[metric] = new SelfMonitoringMetricState(since, alreadyNotified || sustainedEnough);
        }

        Evaluate(
            SelfMonitoringMetric.DiskFree,
            snapshot.DiskFreeBytes < thresholds.DiskFreeBytesMinimum,
            requiresSustain: false,
            severity: () => Severity.Error,
            message: () => $"Disk free space is {snapshot.DiskFreeBytes:N0} bytes, below the {thresholds.DiskFreeBytesMinimum:N0}-byte threshold.",
            fields: () => new Dictionary<string, string> { ["disk_free_bytes"] = snapshot.DiskFreeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        Evaluate(
            SelfMonitoringMetric.DropCounter,
            snapshot.DropCounterDelta > 0,
            requiresSustain: false,
            severity: () => Severity.Warning,
            message: () => $"{snapshot.DropCounterDelta} message(s) were dropped since the last check.",
            fields: () => new Dictionary<string, string> { ["dropped"] = snapshot.DropCounterDelta.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        Evaluate(
            SelfMonitoringMetric.QueueDepthSustained,
            snapshot.QueueDepthPercent >= thresholds.QueueDepthPercentMax,
            requiresSustain: true,
            severity: () => Severity.Warning,
            message: () => $"Ingest queue depth has stayed at or above {thresholds.QueueDepthPercentMax}% for {thresholds.QueueDepthSustainedFor}.",
            fields: () => new Dictionary<string, string> { ["queue_depth_percent"] = snapshot.QueueDepthPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        Evaluate(
            SelfMonitoringMetric.ListenerDown,
            snapshot.ListenersDown.Count > 0,
            requiresSustain: false,
            severity: () => Severity.Error,
            message: () => $"Listener(s) down: {string.Join(", ", snapshot.ListenersDown)}.",
            fields: () => new Dictionary<string, string> { ["listeners_down"] = string.Join(",", snapshot.ListenersDown) });

        Evaluate(
            SelfMonitoringMetric.ArchiveVerificationFailure,
            snapshot.ArchiveVerificationFailures > 0,
            requiresSustain: false,
            severity: () => Severity.Error,
            message: () => $"{snapshot.ArchiveVerificationFailures} archive(s) failed verification since the last check.",
            fields: () => new Dictionary<string, string> { ["failed_count"] = snapshot.ArchiveVerificationFailures.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        return (new SelfMonitoringState(newMetrics), transitions);
    }
}
