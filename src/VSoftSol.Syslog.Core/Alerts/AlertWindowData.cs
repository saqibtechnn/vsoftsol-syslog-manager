namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>
/// The count of matching events for one grouping value within the evaluation window, plus a
/// small sample of the event ids that made it up (the triggering events an
/// <see cref="AlertInstance"/> records). <c>GroupValue</c> is null for an ungrouped alert.
/// </summary>
public sealed record GroupCount(string? GroupValue, long Count, IReadOnlyList<long> SampleEventIds);

/// <summary>
/// When a device (in scope for a <see cref="AlertEvaluationType.DeviceSilent"/>
/// alert) was last heard from, and the silence threshold that applies to it (its own
/// <c>heartbeat_minutes</c> when set, otherwise the alert's window).
/// </summary>
public sealed record DeviceSilence(long DeviceId, string DeviceName, DateTimeOffset? LastSeenUtc, int ThresholdMinutes);

/// <summary>
/// The data an alert evaluation needs, already fetched from storage (hybrid model — SQL
/// aggregates for the common case, an in-memory filtered stream when the alert has a
/// <c>ConditionGroup</c>). Which fields are populated depends on the alert type. Keeping the
/// fetch and the decision separate makes the alert evaluator pure and oracle-testable.
/// </summary>
public sealed record AlertWindowData
{
    /// <summary>Threshold / Absence: the per-group match counts (Absence ⇒ one ungrouped entry).</summary>
    public IReadOnlyList<GroupCount> GroupCounts { get; init; } = [];

    /// <summary>DistinctCount: the number of distinct values of the group-by field seen in the window.</summary>
    public long DistinctValueCount { get; init; }

    /// <summary>DistinctCount: a sample of event ids from the window, for the triggering-event record.</summary>
    public IReadOnlyList<long> DistinctSampleEventIds { get; init; } = [];

    /// <summary>DeviceSilent: one entry per candidate device.</summary>
    public IReadOnlyList<DeviceSilence> DeviceSilences { get; init; } = [];

    /// <summary>
    /// True when the window scan hit its row cap before finishing — the counts are a lower
    /// bound. The evaluator still fires (a truncated count that already breaches is still a
    /// breach); the service raises a diagnostic notification.
    /// </summary>
    public bool Truncated { get; init; }
}
