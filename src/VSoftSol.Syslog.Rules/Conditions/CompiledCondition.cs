using System.Text.RegularExpressions;
using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Rules.Conditions;

/// <summary>
/// A validated, ready-to-evaluate condition tree. Regexes are already compiled with
/// <see cref="RegexOptions.NonBacktracking"/> (linear time — ReDoS is impossible by
/// construction) plus a match timeout as defence in depth.
/// </summary>
public sealed class CompiledCondition
{
    internal CompiledCondition(CompiledNode root) => Root = root;

    internal CompiledNode Root { get; }

    /// <summary>A condition that matches nothing — the safe default for an empty tree.</summary>
    public static CompiledCondition MatchNothing { get; } = new(CompiledConstant.False);
}

internal abstract class CompiledNode;

internal sealed class CompiledConstant(bool value) : CompiledNode
{
    public bool Value { get; } = value;

    public static CompiledConstant False { get; } = new(false);
}

internal sealed class CompiledGroup(ConditionJoin join, IReadOnlyList<CompiledNode> children) : CompiledNode
{
    public ConditionJoin Join { get; } = join;

    public IReadOnlyList<CompiledNode> Children { get; } = children;
}

internal sealed class CompiledComparison(
    ConditionFieldInfo field,
    ConditionOperator op,
    string rawValue,
    double? numericValue,
    IReadOnlyList<string>? listValues,
    Regex? regex) : CompiledNode
{
    public ConditionFieldInfo Field { get; } = field;

    public ConditionOperator Operator { get; } = op;

    public string RawValue { get; } = rawValue;

    public double? NumericValue { get; } = numericValue;

    public IReadOnlyList<string>? ListValues { get; } = listValues;

    public Regex? Regex { get; } = regex;
}

/// <summary>The outcome of compiling a <see cref="ConditionNode"/> tree.</summary>
public sealed record ConditionCompileResult
{
    public bool Success => Errors.Count == 0 && Condition is not null;

    public CompiledCondition? Condition { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ConditionCompileResult Ok(CompiledCondition condition) => new() { Condition = condition };

    public static ConditionCompileResult Fail(IReadOnlyList<string> errors) => new() { Errors = errors };
}
