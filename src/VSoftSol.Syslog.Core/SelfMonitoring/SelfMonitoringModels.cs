namespace VSoftSol.Syslog.Core.SelfMonitoring;

/// <summary>The five collector-health conditions PHASE_11 item 6 requires a self-alert
/// for. "Listener down" is per-listener — <see cref="SelfMonitoringSnapshot.ListenersDown"/>
/// names which ones, so one metric slot covers any number of listeners.</summary>
public enum SelfMonitoringMetric
{
    DiskFree,
    DropCounter,
    QueueDepthSustained,
    ListenerDown,
    ArchiveVerificationFailure,
}

public enum SelfMonitoringTransitionKind
{
    Started,
    Cleared,
}

/// <summary>One tick's raw readings. <see cref="DropCounterDelta"/> and
/// <see cref="ArchiveVerificationFailures"/> are counts observed <em>since the previous
/// tick</em>, not cumulative totals — a self-alert about "a drop happened" must not keep
/// re-firing forever on a total that never resets.</summary>
public sealed record SelfMonitoringSnapshot(
    DateTimeOffset ObservedUtc,
    long DiskFreeBytes,
    long DropCounterDelta,
    int QueueDepthPercent,
    IReadOnlyList<string> ListenersDown,
    int ArchiveVerificationFailures);

public sealed record SelfMonitoringThresholds
{
    public long DiskFreeBytesMinimum { get; init; } = 5_000_000_000; // 5 GB
    public int QueueDepthPercentMax { get; init; } = 80;
    public TimeSpan QueueDepthSustainedFor { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Per-metric breach bookkeeping carried between ticks by the caller (a
/// <c>BackgroundService</c> in Phase 11's Service layer) — this record is the entire state
/// the evaluator needs, so it stays pure and testable with explicit timestamps, no wall
/// clock, no I/O.</summary>
public sealed record SelfMonitoringMetricState(DateTimeOffset BreachedSinceUtc, bool Notified);

public sealed record SelfMonitoringState(IReadOnlyDictionary<SelfMonitoringMetric, SelfMonitoringMetricState> Metrics)
{
    public static SelfMonitoringState Empty { get; } = new(new Dictionary<SelfMonitoringMetric, SelfMonitoringMetricState>());
}

/// <summary>A breach starting or clearing — the caller turns each of these into a synthetic
/// health event on the reserved internal stream (PHASE_11 item 7).</summary>
public sealed record SelfMonitoringTransition(
    SelfMonitoringMetric Metric,
    SelfMonitoringTransitionKind Kind,
    Enums.Severity Severity,
    string Message,
    IReadOnlyDictionary<string, string> Fields);
