namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>
/// How an <see cref="AlertDefinition"/> decides whether to fire (PHASE_08 build item 2).
/// Every type evaluates over a sliding window ending "now"; the scheduler runs it on the
/// definition's interval.
/// </summary>
public enum AlertEvaluationType
{
    /// <summary>
    /// The count of matching events, optionally grouped by a field, exceeds
    /// <see cref="AlertDefinition.Threshold"/> within the window.
    /// </summary>
    Threshold,

    /// <summary>
    /// The number of distinct values of <see cref="AlertDefinition.GroupByField"/> among
    /// matching events exceeds <see cref="AlertDefinition.Threshold"/> within the window.
    /// </summary>
    DistinctCount,

    /// <summary>
    /// No events at all have been received from a device (or any device in the restricted
    /// groups) for longer than the window — a dead switch, a broken syslog config, or a
    /// failed collector path. Honours each device's own <c>heartbeat_minutes</c> when set.
    /// </summary>
    DeviceSilent,

    /// <summary>
    /// No events matching the filter arrived within the window (e.g. a nightly backup that
    /// did not log). The inverse of <see cref="Threshold"/> with an implicit threshold of 0.
    /// </summary>
    Absence,
}
