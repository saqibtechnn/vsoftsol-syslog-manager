using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>
/// One firing of an alert (PHASE_08 build items 5 &amp; 6). At most one non-resolved instance
/// exists per <c>(alert_id, group_value)</c> — that is the deduplication guarantee: a
/// condition that stays true across many evaluations produces exactly one open instance and
/// re-notifies only on the alert's re-notify interval.
/// </summary>
public sealed record AlertInstance
{
    public required long InstanceId { get; init; }

    public required long AlertId { get; init; }

    public string AlertName { get; init; } = string.Empty;

    public NotificationLevel Severity { get; init; }

    public AlertState State { get; init; } = AlertState.Firing;

    /// <summary>The grouping value that breached (host name, source IP, …); null for an ungrouped alert.</summary>
    public string? GroupValue { get; init; }

    public DateTimeOffset OpenedUtc { get; init; }

    /// <summary>The value that breached the threshold at open time (count, distinct count, or silent minutes).</summary>
    public long ObservedValue { get; init; }

    public int Threshold { get; init; }

    public DateTimeOffset? AcknowledgedUtc { get; init; }

    public string? AcknowledgedBy { get; init; }

    public DateTimeOffset? ResolvedUtc { get; init; }

    public string? ResolvedBy { get; init; }

    /// <summary>True when the resolution was automatic (the condition cleared), not an operator action.</summary>
    public bool AutoResolved { get; init; }

    /// <summary>When the actions last ran for this instance — drives the re-notify interval.</summary>
    public DateTimeOffset? LastNotifiedUtc { get; init; }

    /// <summary>The most recent free-text note recorded at a transition.</summary>
    public string? Note { get; init; }

    /// <summary>Ids of the events that triggered this firing — stored, never copied (PHASE_08 item 6).</summary>
    public IReadOnlyList<long> TriggerEventIds { get; init; } = [];

    /// <summary>True while the instance is not resolved.</summary>
    public bool IsOpen => State != AlertState.Resolved;
}
