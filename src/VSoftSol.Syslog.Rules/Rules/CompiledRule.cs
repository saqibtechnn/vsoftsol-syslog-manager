using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Conditions;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>
/// A validated, ready-to-evaluate rule. The filter is already compiled (regexes are
/// <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/> — ReDoS-proof,
/// ADR 0014); actions have passed allow-list / URL-scheme / template validation.
/// </summary>
public sealed class CompiledRule
{
    internal CompiledRule(
        long ruleId,
        string name,
        int priority,
        CompiledCondition filter,
        bool alwaysMatches,
        IReadOnlyList<RuleAction> actions,
        TimeOfDayWindow? window,
        IReadOnlyList<long> deviceGroupIds,
        EscalationPolicy? escalation)
    {
        RuleId = ruleId;
        Name = name;
        Priority = priority;
        Filter = filter;
        AlwaysMatches = alwaysMatches;
        Actions = actions;
        Window = window;
        DeviceGroupIds = deviceGroupIds;
        Escalation = escalation;
    }

    public long RuleId { get; }

    public string Name { get; }

    public int Priority { get; }

    internal CompiledCondition Filter { get; }

    /// <summary>True when the rule has no filter (matches every message).</summary>
    internal bool AlwaysMatches { get; }

    internal IReadOnlyList<RuleAction> Actions { get; }

    internal TimeOfDayWindow? Window { get; }

    internal IReadOnlyList<long> DeviceGroupIds { get; }

    internal EscalationPolicy? Escalation { get; }

    /// <summary>True when any action in the normal or escalation list halts the rule chain.</summary>
    internal bool HasSuppress =>
        Actions.Any(a => a is SuppressAction)
        || (Escalation?.EscalationActions.Any(a => a is SuppressAction) ?? false);
}

/// <summary>One rule that failed to compile — surfaced to the operator, never fatal.</summary>
public sealed record RuleCompileError(long RuleId, string Name, IReadOnlyList<string> Errors);

/// <summary>The compiled rule set, ordered by priority then id.</summary>
public sealed class CompiledRuleSet
{
    internal CompiledRuleSet(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RuleCompileError> errors)
    {
        Rules = rules;
        CompileErrors = errors;
    }

    /// <summary>An empty rule set — the safe default before the first load.</summary>
    public static CompiledRuleSet Empty { get; } = new([], []);

    public IReadOnlyList<CompiledRule> Rules { get; }

    public IReadOnlyList<RuleCompileError> CompileErrors { get; }

    public int RuleCount => Rules.Count;
}
