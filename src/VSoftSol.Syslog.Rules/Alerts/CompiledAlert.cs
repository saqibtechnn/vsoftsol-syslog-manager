using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Conditions;

namespace VSoftSol.Syslog.Rules.Alerts;

/// <summary>
/// A validated, ready-to-schedule alert (PHASE_08). Mirrors <c>CompiledRule</c>: the filter
/// is compiled once, the group-by field is resolved once, and the actions are carried
/// through unchanged for the dispatcher.
/// </summary>
public sealed class CompiledAlert
{
    internal CompiledAlert(
        long alertId,
        string name,
        NotificationLevel severity,
        AlertEvaluationType type,
        CompiledCondition filter,
        bool alwaysMatches,
        int windowSeconds,
        int intervalSeconds,
        string? groupByField,
        int threshold,
        IReadOnlyList<RuleAction> actions,
        IReadOnlyList<long> deviceGroupIds,
        IReadOnlyList<long> streamIds,
        int reNotifySeconds,
        bool autoResolve)
    {
        AlertId = alertId;
        Name = name;
        Severity = severity;
        Type = type;
        Filter = filter;
        AlwaysMatches = alwaysMatches;
        WindowSeconds = windowSeconds;
        IntervalSeconds = intervalSeconds;
        GroupByField = groupByField;
        Threshold = threshold;
        Actions = actions;
        DeviceGroupIds = deviceGroupIds;
        StreamIds = streamIds;
        ReNotifySeconds = reNotifySeconds;
        AutoResolve = autoResolve;
    }

    public long AlertId { get; }

    public string Name { get; }

    public NotificationLevel Severity { get; }

    public AlertEvaluationType Type { get; }

    internal CompiledCondition Filter { get; }

    /// <summary>True when the alert has no filter — every event in the window counts.</summary>
    public bool AlwaysMatches { get; }

    public int WindowSeconds { get; }

    public int IntervalSeconds { get; }

    /// <summary>Resolved built-in field name (<c>hostname</c>, …) or <c>field.&lt;name&gt;</c>; null ⇒ ungrouped.</summary>
    public string? GroupByField { get; }

    public int Threshold { get; }

    public IReadOnlyList<RuleAction> Actions { get; }

    public IReadOnlyList<long> DeviceGroupIds { get; }

    public IReadOnlyList<long> StreamIds { get; }

    public int ReNotifySeconds { get; }

    public bool AutoResolve { get; }

    /// <summary>Evaluates the compiled filter against one candidate event (the in-memory path).</summary>
    public bool FilterMatches(Core.Events.SyslogEvent syslogEvent) =>
        AlwaysMatches || ConditionEvaluator.Matches(Filter, syslogEvent);
}

/// <summary>One alert that would not compile, and why. Never fatal to the set.</summary>
public sealed record AlertCompileError(long AlertId, string Name, IReadOnlyList<string> Errors);

/// <summary>The compiled alert set the scheduler evaluates (mirrors <c>CompiledRuleSet</c>).</summary>
public sealed class CompiledAlertSet
{
    internal CompiledAlertSet(IReadOnlyList<CompiledAlert> alerts, IReadOnlyList<AlertCompileError> compileErrors)
    {
        Alerts = alerts;
        CompileErrors = compileErrors;
    }

    public static CompiledAlertSet Empty { get; } = new([], []);

    public IReadOnlyList<CompiledAlert> Alerts { get; }

    public IReadOnlyList<AlertCompileError> CompileErrors { get; }

    public int AlertCount => Alerts.Count;
}
