using System.Globalization;
using System.Text;
using VSoftSol.Syslog.Core.Search;

namespace VSoftSol.Syslog.UnitTests.Search;

/// <summary>
/// Renders a <see cref="QueryNode"/> tree as a compact S-expression so a large parser
/// matrix can assert on tree shape with one string compare per case.
/// </summary>
internal static class SearchAstDescriber
{
    public static string Describe(QueryNode node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(QueryNode node, StringBuilder sb)
    {
        switch (node)
        {
            case MatchAllNode:
                sb.Append('*');
                break;
            case TextTermNode t:
                sb.Append("(text \"").Append(t.Text).Append('"');
                if (t.Prefix)
                {
                    sb.Append(" prefix");
                }

                sb.Append(')');
                break;
            case FieldTermNode f:
                sb.Append("(field ").Append(f.Field.CanonicalName).Append(' ')
                    .Append(Symbol(f.Operator)).Append(" \"").Append(f.Value).Append('"');
                if (f.Prefix)
                {
                    sb.Append(" prefix");
                }

                sb.Append(')');
                break;
            case AndNode a:
                sb.Append("(and ");
                Write(a.Left, sb);
                sb.Append(' ');
                Write(a.Right, sb);
                sb.Append(')');
                break;
            case OrNode o:
                sb.Append("(or ");
                Write(o.Left, sb);
                sb.Append(' ');
                Write(o.Right, sb);
                sb.Append(')');
                break;
            case NotNode n:
                sb.Append("(not ");
                Write(n.Operand, sb);
                sb.Append(')');
                break;
            default:
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"unhandled node {node.GetType().Name}"));
        }
    }

    private static string Symbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equals => "=",
        ComparisonOperator.NotEquals => "!=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => "?",
    };
}
