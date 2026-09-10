using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Search;

namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>The outcome of translating a saved-search query into an alert filter.</summary>
/// <param name="Filter">
/// The best-effort <see cref="ConditionGroup"/>, or null when nothing could be translated.
/// </param>
/// <param name="Unsupported">
/// Human-readable notes for every part of the query that could not be represented exactly —
/// the operator is asked to review the filter in the builder before saving.
/// </param>
public sealed record SearchConditionTranslation(ConditionGroup? Filter, IReadOnlyList<string> Unsupported)
{
    /// <summary>True when the whole query translated without loss.</summary>
    public bool Exact => Unsupported.Count == 0;
}

/// <summary>
/// Converts a Phase 5 search-query AST into a Phase 6 <see cref="ConditionGroup"/> for the
/// "promote a saved search to an alert" flow (PHASE_08 build item 7). The two languages do
/// not overlap perfectly — reference fields (<c>device:</c>, <c>stream:</c>), timestamp
/// ranges, wildcards, and <c>&gt;=</c>/<c>&lt;=</c> have no <see cref="ConditionComparison"/>
/// equivalent — so anything lossy is reported in
/// <see cref="SearchConditionTranslation.Unsupported"/> rather than dropped silently. Pure;
/// no I/O.
/// </summary>
public static class SearchToConditionTranslator
{
    private static readonly Dictionary<string, string> FieldMap = new(StringComparer.Ordinal)
    {
        ["message"] = "message",
        ["host"] = "hostname",
        ["source_ip"] = "source_ip",
        ["app"] = "app",
        ["proc_id"] = "proc_id",
        ["msg_id"] = "msg_id",
        ["severity"] = "severity",
        ["facility"] = "facility",
        ["vendor"] = "vendor",
        ["protocol"] = "protocol",
        ["parse_status"] = "parse_status",
    };

    public static SearchConditionTranslation Translate(QueryNode? query)
    {
        var unsupported = new List<string>();
        if (query is null or MatchAllNode)
        {
            return new SearchConditionTranslation(null, unsupported);
        }

        ConditionNode? node = Convert(query, negated: false, unsupported);
        ConditionGroup? group = node switch
        {
            null => null,
            ConditionGroup g => g,
            _ => new ConditionGroup { Join = ConditionJoin.And, Children = [node] },
        };

        return new SearchConditionTranslation(group, unsupported);
    }

    private static ConditionNode? Convert(QueryNode query, bool negated, List<string> unsupported)
    {
        switch (query)
        {
            case MatchAllNode:
                return null;

            case TextTermNode text:
                if (text.Prefix)
                {
                    unsupported.Add($"the wildcard in \"{text.Text}*\" became a plain \"contains\"");
                }

                return new ConditionComparison
                {
                    Field = "message",
                    Operator = negated ? ConditionOperator.NotContains : ConditionOperator.Contains,
                    Value = text.Text,
                };

            case FieldTermNode field:
                return ConvertField(field, negated, unsupported);

            case AndNode and:
                return ConvertGroup(Flatten(and), negated ? ConditionJoin.Or : ConditionJoin.And, negated, unsupported);

            case OrNode or:
                return ConvertGroup(Flatten(or), negated ? ConditionJoin.And : ConditionJoin.Or, negated, unsupported);

            case NotNode not:
                return Convert(not.Operand, !negated, unsupported);

            default:
                unsupported.Add($"a {query.GetType().Name} part of the query was dropped");
                return null;
        }
    }

    private static ConditionNode? ConvertGroup(
        IReadOnlyList<QueryNode> parts, ConditionJoin join, bool negated, List<string> unsupported)
    {
        var children = new List<ConditionNode>(parts.Count);
        foreach (QueryNode part in parts)
        {
            ConditionNode? child = Convert(part, negated, unsupported);
            if (child is not null)
            {
                children.Add(child);
            }
        }

        return children.Count switch
        {
            0 => null,
            1 => children[0],
            _ => new ConditionGroup { Join = join, Children = children },
        };
    }

    private static ConditionComparison? ConvertField(FieldTermNode field, bool negated, List<string> unsupported)
    {
        if (!FieldMap.TryGetValue(field.Field.CanonicalName, out string? mapped))
        {
            if (field.Field.ValueType == SearchValueType.CustomField)
            {
                mapped = ConditionFields.ExtractedFieldPrefix + field.Field.CustomFieldName;
            }
            else
            {
                unsupported.Add($"\"{field.Field.CanonicalName}:\" has no alert-filter equivalent and was dropped");
                return null;
            }
        }

        ConditionOperator op;
        switch (field.Operator)
        {
            case ComparisonOperator.Equals:
                op = negated ? ConditionOperator.NotEquals : ConditionOperator.Equals;
                if (field.Prefix)
                {
                    op = negated ? ConditionOperator.NotContains : ConditionOperator.StartsWith;
                    unsupported.Add($"the wildcard in \"{field.Field.CanonicalName}:{field.Value}*\" became \"starts with\"");
                }

                break;
            case ComparisonOperator.NotEquals:
                op = negated ? ConditionOperator.Equals : ConditionOperator.NotEquals;
                break;
            case ComparisonOperator.GreaterThan:
            case ComparisonOperator.GreaterThanOrEqual:
                op = ConditionOperator.GreaterThan;
                if (field.Operator == ComparisonOperator.GreaterThanOrEqual)
                {
                    unsupported.Add($"\"{field.Field.CanonicalName}:>={field.Value}\" became \">\" ({field.Value})");
                }

                if (negated)
                {
                    unsupported.Add($"a negated range on \"{field.Field.CanonicalName}\" was dropped");
                    return null;
                }

                break;
            case ComparisonOperator.LessThan:
            case ComparisonOperator.LessThanOrEqual:
                op = ConditionOperator.LessThan;
                if (field.Operator == ComparisonOperator.LessThanOrEqual)
                {
                    unsupported.Add($"\"{field.Field.CanonicalName}:<={field.Value}\" became \"<\" ({field.Value})");
                }

                if (negated)
                {
                    unsupported.Add($"a negated range on \"{field.Field.CanonicalName}\" was dropped");
                    return null;
                }

                break;
            default:
                unsupported.Add($"operator on \"{field.Field.CanonicalName}\" was dropped");
                return null;
        }

        return new ConditionComparison { Field = mapped, Operator = op, Value = field.Value };
    }

    private static List<QueryNode> Flatten(AndNode root)
    {
        var parts = new List<QueryNode>();
        Walk(root);
        return parts;

        void Walk(QueryNode n)
        {
            if (n is AndNode a)
            {
                Walk(a.Left);
                Walk(a.Right);
            }
            else
            {
                parts.Add(n);
            }
        }
    }

    private static List<QueryNode> Flatten(OrNode root)
    {
        var parts = new List<QueryNode>();
        Walk(root);
        return parts;

        void Walk(QueryNode n)
        {
            if (n is OrNode o)
            {
                Walk(o.Left);
                Walk(o.Right);
            }
            else
            {
                parts.Add(n);
            }
        }
    }
}
