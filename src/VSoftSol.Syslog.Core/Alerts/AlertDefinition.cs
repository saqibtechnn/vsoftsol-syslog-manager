using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>
/// An alert as authored in the UI and stored in the <c>alert_definitions</c> table
/// (PHASE_08 build item 1). The <see cref="Filter"/> reuses the Phase 6
/// <see cref="ConditionGroup"/>; the <see cref="Actions"/> reuse the Phase 7
/// <see cref="RuleAction"/> model and run when the alert transitions into
/// <see cref="AlertState.Firing"/> (and again on the re-notify interval).
/// </summary>
public sealed class AlertDefinition
{
    public long AlertId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Severity shown in the notification centre and used to sort the open-alert list.</summary>
    public NotificationLevel Severity { get; set; } = NotificationLevel.Warning;

    public bool Enabled { get; set; } = true;

    public AlertEvaluationType Type { get; set; } = AlertEvaluationType.Threshold;

    /// <summary>Null / empty group ⇒ every event counts. Ignored for <see cref="AlertEvaluationType.DeviceSilent"/>.</summary>
    public ConditionGroup? Filter { get; set; }

    /// <summary>The sliding window the evaluation looks back over, in seconds.</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>How often the scheduler evaluates this alert, in seconds.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// For <see cref="AlertEvaluationType.Threshold"/>: the field whose value groups the
    /// counts (null / <c>none</c> ⇒ one count for the whole window). For
    /// <see cref="AlertEvaluationType.DistinctCount"/>: the field whose distinct values are
    /// counted (required). Unused otherwise.
    /// </summary>
    public string? GroupByField { get; set; }

    /// <summary>Fire when the observed value is strictly greater than this. Unused for Absence / DeviceSilent.</summary>
    public int Threshold { get; set; } = 5;

    /// <summary>Free text shown on the firing alert — what the operator should do.</summary>
    public string? RemediationNotes { get; set; }

    public List<RuleAction> Actions { get; set; } = [];

    /// <summary>Empty ⇒ every device. Otherwise only devices in these groups are considered.</summary>
    public List<long> DeviceGroupIds { get; set; } = [];

    /// <summary>Empty ⇒ every stream. Otherwise only events routed to these streams count.</summary>
    public List<long> StreamIds { get; set; } = [];

    /// <summary>
    /// Once an instance is open, re-run the actions only this often (seconds) — a firing
    /// alert does not re-notify every evaluation (PHASE_08 build item 4). 0 ⇒ never re-notify.
    /// </summary>
    public int ReNotifySeconds { get; set; } = 3600;

    /// <summary>Auto-resolve an open instance when its condition clears (PHASE_08 build item 5).</summary>
    public bool AutoResolve { get; set; } = true;

    /// <summary>True for a seeded template that was cloned as-is; blocks delete, not edit.</summary>
    public bool IsSystem { get; set; }

    public long HitCount { get; set; }

    public DateTimeOffset? LastEvaluatedUtc { get; set; }

    public DateTimeOffset? LastFiredUtc { get; set; }
}
