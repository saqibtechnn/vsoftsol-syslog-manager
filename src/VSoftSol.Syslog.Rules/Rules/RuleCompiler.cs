using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Conditions;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>
/// Host-supplied limits the compiler needs to validate actions that touch the local
/// machine or network (PHASE_07 security). Supplied by the composition root, never by a
/// rule author.
/// </summary>
/// <param name="ScriptAllowListDirectories">Absolute directories a <c>RunScript</c> executable may live under.</param>
/// <param name="FileActionBaseDirectory">Absolute base directory <c>WriteToFile</c> is confined to.</param>
/// <param name="LocalSyslogEndpoints">The collector's own listener endpoints — a forward target here is a loop.</param>
public sealed record RuleCompileOptions(
    IReadOnlyList<string> ScriptAllowListDirectories,
    string? FileActionBaseDirectory,
    IReadOnlyList<string> LocalSyslogEndpoints)
{
    public static RuleCompileOptions Empty { get; } = new([], null, []);
}

/// <summary>The outcome of compiling a <see cref="RuleDefinition"/>.</summary>
public sealed record RuleCompileResult
{
    public bool Success => Errors.Count == 0 && Rule is not null;

    public CompiledRule? Rule { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static RuleCompileResult Ok(CompiledRule rule) => new() { Rule = rule };

    public static RuleCompileResult Fail(IReadOnlyList<string> errors) => new() { Errors = errors };
}

/// <summary>
/// Validates a <see cref="RuleDefinition"/> and compiles it once for repeated evaluation on
/// the ingest path. Action validation here is the first of two layers — every executor
/// re-checks its own inputs at run time (defence in depth for the security matrices).
/// </summary>
public sealed class RuleCompiler
{
    private const int MaxActionsPerRule = RuleActionValidator.MaxActionsPerRule;

    private readonly ConditionCompiler _conditions = new();
    private readonly RuleCompileOptions _options;

    public RuleCompiler(RuleCompileOptions? options = null) => _options = options ?? RuleCompileOptions.Empty;

    /// <summary>
    /// Compiles a whole rule set: each rule is compiled, the failures are collected (never
    /// fatal), and the survivors are ordered by priority then id — exactly what the
    /// ingest-path <see cref="RuleSet"/> expects.
    /// </summary>
    public CompiledRuleSet CompileSet(IEnumerable<RuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var compiled = new List<CompiledRule>();
        var errors = new List<RuleCompileError>();

        foreach (RuleDefinition rule in rules)
        {
            RuleCompileResult result = Compile(rule);
            if (result.Success)
            {
                compiled.Add(result.Rule!);
            }
            else
            {
                errors.Add(new RuleCompileError(rule.RuleId, rule.Name, result.Errors));
            }
        }

        compiled.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.RuleId.CompareTo(b.RuleId));
        return new CompiledRuleSet(compiled, errors);
    }

    public RuleCompileResult Compile(RuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add("The rule needs a name.");
        }

        bool alwaysMatches = rule.Filter is null || rule.Filter.Children.Count == 0;
        CompiledCondition filter = CompiledCondition.MatchNothing;
        if (!alwaysMatches)
        {
            ConditionCompileResult compiled = _conditions.Compile(rule.Filter);
            if (compiled.Success)
            {
                filter = compiled.Condition!;
            }
            else
            {
                errors.AddRange(compiled.Errors.Select(e => "Filter: " + e));
            }
        }

        if (rule.Actions.Count == 0)
        {
            errors.Add("The rule has no actions.");
        }
        else if (rule.Actions.Count > MaxActionsPerRule)
        {
            errors.Add($"Too many actions ({rule.Actions.Count}); the limit is {MaxActionsPerRule}.");
        }

        for (int i = 0; i < rule.Actions.Count; i++)
        {
            RuleActionValidator.Validate(rule.Actions[i], $"Action {i + 1}", _options, errors);
        }

        if (rule.Window is { } w)
        {
            if (w.StartMinute is < 0 or > 1439 || w.EndMinute is < 0 or > 1439)
            {
                errors.Add("Time window minutes must be between 0 and 1439.");
            }
        }

        if (rule.Escalation is { } esc)
        {
            if (esc.Threshold < 2)
            {
                errors.Add("Escalation threshold must be at least 2.");
            }

            if (esc.WindowSeconds < 1)
            {
                errors.Add("Escalation window must be at least 1 second.");
            }

            if (esc.EscalationActions.Count == 0)
            {
                errors.Add("Escalation is enabled but has no actions.");
            }

            for (int i = 0; i < esc.EscalationActions.Count; i++)
            {
                RuleActionValidator.Validate(esc.EscalationActions[i], $"Escalation action {i + 1}", _options, errors);
            }
        }

        if (errors.Count > 0)
        {
            return RuleCompileResult.Fail(errors);
        }

        return RuleCompileResult.Ok(new CompiledRule(
            rule.RuleId,
            rule.Name.Trim(),
            rule.Priority,
            filter,
            alwaysMatches,
            rule.Actions,
            rule.Window,
            rule.DeviceGroupIds,
            rule.Escalation));
    }

}
