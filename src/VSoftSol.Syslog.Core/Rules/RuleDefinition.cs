using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Core.Rules;

/// <summary>
/// A rule as authored in the UI and stored in the <c>rules</c> table (PHASE_07 item 1).
/// The <see cref="Filter"/> reuses the Phase 6 <see cref="ConditionGroup"/>; the
/// <see cref="Actions"/> run in list order when the filter matches and the rule is in its
/// time window and device-group scope.
/// </summary>
public sealed class RuleDefinition
{
    public long RuleId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Lower runs first. Rules with equal priority run in id order.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Null / empty group ⇒ the rule matches every message.</summary>
    public ConditionGroup? Filter { get; set; }

    public List<RuleAction> Actions { get; set; } = [];

    /// <summary>Null ⇒ the rule is always in window.</summary>
    public TimeOfDayWindow? Window { get; set; }

    /// <summary>Empty ⇒ the rule applies to every device. Otherwise only these groups.</summary>
    public List<long> DeviceGroupIds { get; set; } = [];

    /// <summary>Null ⇒ no escalation.</summary>
    public EscalationPolicy? Escalation { get; set; }

    public long HitCount { get; set; }

    public DateTimeOffset? LastFiredUtc { get; set; }

    /// <summary>True for a seeded template that was cloned as-is; blocks delete, not edit.</summary>
    public bool IsSystem { get; set; }
}

/// <summary>
/// An optional time-of-day gate on a rule (PHASE_07 item 1). Times are minutes since
/// midnight in the collector's local zone; a window that wraps midnight
/// (<see cref="StartMinute"/> &gt; <see cref="EndMinute"/>) is supported.
/// </summary>
/// <param name="StartMinute">0-1439 inclusive.</param>
/// <param name="EndMinute">0-1439 inclusive.</param>
/// <param name="Days">Days the window is active; empty ⇒ every day.</param>
public sealed record TimeOfDayWindow(int StartMinute, int EndMinute, IReadOnlyList<DayOfWeek> Days)
{
    /// <summary>True when <paramref name="localNow"/> falls inside the window.</summary>
    public bool Contains(DateTimeOffset localNow)
    {
        if (Days.Count > 0 && !Days.Contains(localNow.DayOfWeek))
        {
            return false;
        }

        int minute = (localNow.Hour * 60) + localNow.Minute;
        return StartMinute <= EndMinute
            ? minute >= StartMinute && minute < EndMinute
            : minute >= StartMinute || minute < EndMinute; // wraps midnight
    }
}

/// <summary>
/// Escalation policy (PHASE_07 item 4): when a rule's filter matches
/// <see cref="Threshold"/> times within <see cref="WindowSeconds"/>, run
/// <see cref="EscalationActions"/> instead of the normal action list, once per window.
/// </summary>
public sealed class EscalationPolicy
{
    public int Threshold { get; set; } = 5;

    public int WindowSeconds { get; set; } = 300;

    public List<RuleAction> EscalationActions { get; set; } = [];
}
