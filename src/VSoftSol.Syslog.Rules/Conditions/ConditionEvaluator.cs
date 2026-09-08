using System.Text.RegularExpressions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Rules.Conditions;

/// <summary>
/// Evaluates a <see cref="CompiledCondition"/> against a <see cref="SyslogEvent"/>. Pure
/// and allocation-light so it can run on every message in the ingest path. A regex whose
/// match times out at evaluation time is treated as a non-match (fail closed) and reported
/// through <see cref="EvaluationResult.RegexTimedOut"/> — it never throws and never hangs.
/// </summary>
public static class ConditionEvaluator
{
    public static bool Matches(CompiledCondition condition, SyslogEvent syslogEvent)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(syslogEvent);
        return Eval(condition.Root, syslogEvent, out _);
    }

    /// <summary>As <see cref="Matches(CompiledCondition, SyslogEvent)"/>, also reporting whether a regex timed out.</summary>
    public static EvaluationResult Evaluate(CompiledCondition condition, SyslogEvent syslogEvent)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(syslogEvent);
        bool matched = Eval(condition.Root, syslogEvent, out bool timedOut);
        return new EvaluationResult(matched, timedOut);
    }

    private static bool Eval(CompiledNode node, SyslogEvent e, out bool regexTimedOut)
    {
        switch (node)
        {
            case CompiledConstant k:
                regexTimedOut = false;
                return k.Value;

            case CompiledGroup g:
                {
                    regexTimedOut = false;
                    bool isAnd = g.Join == ConditionJoin.And;
                    foreach (CompiledNode child in g.Children)
                    {
                        bool childMatch = Eval(child, e, out bool childTimeout);
                        regexTimedOut |= childTimeout;
                        if (isAnd && !childMatch)
                        {
                            return false;
                        }

                        if (!isAnd && childMatch)
                        {
                            return true;
                        }
                    }

                    return isAnd; // AND: none failed → true. OR: none matched → false.
                }

            case CompiledComparison c:
                return EvalComparison(c, e, out regexTimedOut);

            default:
                regexTimedOut = false;
                return false;
        }
    }

    private static bool EvalComparison(CompiledComparison c, SyslogEvent e, out bool regexTimedOut)
    {
        regexTimedOut = false;
        IReadOnlyList<string> values = EventFieldReader.Values(c.Field, e);

        switch (c.Operator)
        {
            case ConditionOperator.Exists:
                return values.Count > 0;
            case ConditionOperator.NotExists:
                return values.Count == 0;
        }

        switch (c.Operator)
        {
            case ConditionOperator.Equals:
                return values.Any(v => Eq(v, c.RawValue));
            case ConditionOperator.NotEquals:
                return !values.Any(v => Eq(v, c.RawValue));
            case ConditionOperator.Contains:
                return values.Any(v => v.Contains(c.RawValue, StringComparison.OrdinalIgnoreCase));
            case ConditionOperator.NotContains:
                return !values.Any(v => v.Contains(c.RawValue, StringComparison.OrdinalIgnoreCase));
            case ConditionOperator.StartsWith:
                return values.Any(v => v.StartsWith(c.RawValue, StringComparison.OrdinalIgnoreCase));
            case ConditionOperator.EndsWith:
                return values.Any(v => v.EndsWith(c.RawValue, StringComparison.OrdinalIgnoreCase));
            case ConditionOperator.InList:
                return values.Any(v => c.ListValues!.Any(item => Eq(v, item)));
            case ConditionOperator.GreaterThan:
                {
                    double? actual = EventFieldReader.Numeric(c.Field, e);
                    return actual is { } a && a > c.NumericValue!.Value;
                }

            case ConditionOperator.LessThan:
                {
                    double? actual = EventFieldReader.Numeric(c.Field, e);
                    return actual is { } a && a < c.NumericValue!.Value;
                }

            case ConditionOperator.Matches:
                {
                    foreach (string v in values)
                    {
                        try
                        {
                            if (c.Regex!.IsMatch(v))
                            {
                                return true;
                            }
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            regexTimedOut = true; // fail closed — this comparison does not match
                        }
                    }

                    return false;
                }

            default:
                return false;
        }
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The result of an evaluation, including whether a regex timed out (fail-closed).</summary>
public readonly record struct EvaluationResult(bool Matched, bool RegexTimedOut);
