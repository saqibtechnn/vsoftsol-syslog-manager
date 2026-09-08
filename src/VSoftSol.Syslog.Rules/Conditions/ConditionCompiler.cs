using System.Globalization;
using System.Text.RegularExpressions;
using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Rules.Conditions;

/// <summary>
/// Validates a <see cref="ConditionNode"/> tree and compiles it once for repeated
/// evaluation on the ingest path. Every <c>Matches</c> operator is compiled with
/// <see cref="RegexOptions.NonBacktracking"/> — a user-authored catastrophic-backtracking
/// pattern is impossible to construct (SECURITY_STANDARDS / PHASE_06 ReDoS suite); a
/// pattern that needs a non-linear feature (backreference, lookaround) is rejected with a
/// clear message rather than silently accepted.
/// </summary>
public sealed class ConditionCompiler
{
    /// <summary>Match timeout applied to every compiled regex, as defence in depth.</summary>
    public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

    private const int MaxNodes = 200;
    private const int MaxDepth = 12;

    public ConditionCompileResult Compile(ConditionNode? root)
    {
        if (root is null)
        {
            return ConditionCompileResult.Ok(CompiledCondition.MatchNothing);
        }

        var errors = new List<string>();
        int nodeCount = 0;

        CompiledNode? compiled = CompileNode(root, 0, errors, ref nodeCount);

        if (nodeCount > MaxNodes)
        {
            errors.Add($"The condition has too many parts ({nodeCount}); the limit is {MaxNodes}.");
        }

        if (errors.Count > 0 || compiled is null)
        {
            return ConditionCompileResult.Fail(errors.Count > 0 ? errors : ["The condition could not be compiled."]);
        }

        return ConditionCompileResult.Ok(new CompiledCondition(compiled));
    }

    private CompiledNode? CompileNode(ConditionNode node, int depth, List<string> errors, ref int nodeCount)
    {
        nodeCount++;
        if (depth > MaxDepth)
        {
            errors.Add($"The condition is nested too deeply (limit {MaxDepth}).");
            return null;
        }

        switch (node)
        {
            case ConditionGroup group:
                {
                    if (group.Children.Count == 0)
                    {
                        // An empty group matches nothing — a stream with no rule must not swallow every event.
                        return CompiledConstant.False;
                    }

                    var children = new List<CompiledNode>(group.Children.Count);
                    foreach (ConditionNode child in group.Children)
                    {
                        CompiledNode? c = CompileNode(child, depth + 1, errors, ref nodeCount);
                        if (c is not null)
                        {
                            children.Add(c);
                        }
                    }

                    return children.Count == 0 ? CompiledConstant.False : new CompiledGroup(group.Join, children);
                }

            case ConditionComparison comparison:
                return CompileComparison(comparison, errors);

            default:
                errors.Add($"Unknown condition node '{node.GetType().Name}'.");
                return null;
        }
    }

    private CompiledComparison? CompileComparison(ConditionComparison c, List<string> errors)
    {
        if (!ConditionFields.TryResolve(c.Field, out ConditionFieldInfo? field))
        {
            errors.Add($"Unknown field '{c.Field}'.");
            return null;
        }

        bool takesValue = c.Operator is not (ConditionOperator.Exists or ConditionOperator.NotExists);
        if (takesValue && string.IsNullOrEmpty(c.Value))
        {
            errors.Add($"'{field.Label}' {Describe(c.Operator)} needs a value.");
            return null;
        }

        double? numeric = null;
        IReadOnlyList<string>? list = null;
        Regex? regex = null;

        switch (c.Operator)
        {
            case ConditionOperator.GreaterThan or ConditionOperator.LessThan:
                if (!double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    errors.Add($"'{field.Label}' {Describe(c.Operator)} needs a number, got '{c.Value}'.");
                    return null;
                }

                numeric = n;
                break;

            case ConditionOperator.InList:
                list = c.Value
                    .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
                if (list.Count == 0)
                {
                    errors.Add($"'{field.Label}' is one of — provide at least one value.");
                    return null;
                }

                break;

            case ConditionOperator.Matches:
                try
                {
                    regex = new Regex(
                        c.Value,
                        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                        RegexMatchTimeout);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // NonBacktracking rejects backreferences, lookarounds, atomic groups, and
                    // patterns whose automaton would be huge — exactly the shapes that enable
                    // ReDoS. ArgumentException also covers a syntactically invalid pattern.
                    errors.Add(
                        $"Pattern rejected: {ex.Message.Split('\n')[0].Trim()} " +
                        "Stream regex rules must be linear-time (no backreferences, lookarounds, or atomic groups).");
                    return null;
                }

                break;
        }

        return new CompiledComparison(field, c.Operator, c.Value, numeric, list, regex);
    }

    private static string Describe(ConditionOperator op) => op switch
    {
        ConditionOperator.Equals => "equals",
        ConditionOperator.NotEquals => "does not equal",
        ConditionOperator.Contains => "contains",
        ConditionOperator.NotContains => "does not contain",
        ConditionOperator.StartsWith => "starts with",
        ConditionOperator.EndsWith => "ends with",
        ConditionOperator.Matches => "matches",
        ConditionOperator.GreaterThan => "is greater than",
        ConditionOperator.LessThan => "is less than",
        ConditionOperator.InList => "is one of",
        ConditionOperator.Exists => "exists",
        ConditionOperator.NotExists => "does not exist",
        _ => op.ToString(),
    };
}
