using System.Globalization;
using System.Text;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// A deliberately naive, in-memory matcher for a parsed query against a single
/// <see cref="SyslogEvent"/>. This is the golden oracle (TESTING_STANDARDS "Validation &amp;
/// Evidence"): the SQL compiler's result set must be identical to filtering the same
/// events with this evaluator. Correctness over speed — it does no indexing.
/// </summary>
/// <remarks>
/// Reference resolution (device name → id, stream name → id) is not available here, so the
/// caller supplies a <see cref="QueryEvaluationContext"/> mapping the event's device and
/// stream ids to their names. Free-text matching mirrors <see cref="FtsTokenizer"/>.
/// Null-column and NOT-EXISTS rules mirror SQLite three-valued logic so the two paths agree.
/// </remarks>
public static class QueryEvaluator
{
    private static readonly StringComparison IC = StringComparison.OrdinalIgnoreCase;

    public static bool Matches(QueryNode node, SyslogEvent syslogEvent, QueryEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(syslogEvent);
        ArgumentNullException.ThrowIfNull(context);
        return Eval(node, syslogEvent, context);
    }

    private static bool Eval(QueryNode node, SyslogEvent e, QueryEvaluationContext ctx) => node switch
    {
        MatchAllNode => true,
        NotNode n => !Eval(n.Operand, e, ctx),
        AndNode a => Eval(a.Left, e, ctx) && Eval(a.Right, e, ctx),
        OrNode o => Eval(o.Left, e, ctx) || Eval(o.Right, e, ctx),
        TextTermNode t => MatchText(t.Text, t.Prefix, e),
        FieldTermNode f => MatchField(f, e, ctx),
        _ => throw new InvalidOperationException($"Unhandled query node {node.GetType().Name}."),
    };

    // --------------------------------------------------------------- free text

    private static bool MatchText(string text, bool prefix, SyslogEvent e)
    {
        IReadOnlyList<string> query = FtsTokenizer.Tokenize(text);
        if (query.Count == 0)
        {
            return false; // an FTS MATCH with no tokens matches nothing
        }

        IReadOnlyList<string> doc = FtsTokenizer.Tokenize(SearchableText(e));
        return PhraseMatch(doc, query, prefix);
    }

    private static string SearchableText(SyslogEvent e) =>
        e.Message.Length > 0 ? e.Message : Encoding.UTF8.GetString(e.RawMessage.Span);

    private static bool PhraseMatch(IReadOnlyList<string> doc, IReadOnlyList<string> query, bool prefix)
    {
        int m = query.Count;
        for (int i = 0; i + m <= doc.Count; i++)
        {
            bool ok = true;
            for (int k = 0; k < m; k++)
            {
                bool last = k == m - 1;
                bool tokenMatch = last && prefix
                    ? doc[i + k].StartsWith(query[k], StringComparison.Ordinal)
                    : string.Equals(doc[i + k], query[k], StringComparison.Ordinal);
                if (!tokenMatch)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    // --------------------------------------------------------------- fields

    private static bool MatchField(FieldTermNode f, SyslogEvent e, QueryEvaluationContext ctx) => f.Field.ValueType switch
    {
        SearchValueType.FreeText => Negate(f.Operator, MatchText(f.Value, f.Prefix, e)),
        SearchValueType.Text or SearchValueType.IpAddress => StringColumn(f, ColumnValue(f.Field.CanonicalName, e)),
        SearchValueType.Severity => NumberCompare(f.Operator, (int)e.Severity, long.Parse(f.Value, CultureInfo.InvariantCulture)),
        SearchValueType.Facility => NumberCompare(f.Operator, (int)e.Facility, long.Parse(f.Value, CultureInfo.InvariantCulture)),
        SearchValueType.Number => NumberField(f, e),
        SearchValueType.Timestamp => TimestampField(f, e),
        SearchValueType.Protocol => StringColumn(f, ProtocolToken(e.Protocol)),
        SearchValueType.ParseStatus => StringColumn(f, ParseStatusToken(e.ParseStatus)),
        SearchValueType.Reference => ReferenceField(f, ctx),
        SearchValueType.CustomField => CustomField(f, e),
        _ => false,
    };

    private static bool Negate(ComparisonOperator op, bool value) =>
        op == ComparisonOperator.NotEquals ? !value : value;

    private static string? ColumnValue(string canonical, SyslogEvent e) => canonical switch
    {
        "host" => e.Hostname,
        "source_ip" => e.SourceIp,
        "app" => e.AppName,
        "proc_id" => e.ProcId,
        "msg_id" => e.MsgId,
        "vendor" => e.Vendor,
        _ => null,
    };

    private static bool StringColumn(FieldTermNode f, string? actual)
    {
        if (f.Operator == ComparisonOperator.NotEquals)
        {
            return actual is not null && !string.Equals(actual, f.Value, IC);
        }

        if (actual is null)
        {
            return false;
        }

        return f.Prefix
            ? actual.StartsWith(f.Value, IC)
            : string.Equals(actual, f.Value, IC);
    }

    private static bool NumberField(FieldTermNode f, SyslogEvent e)
    {
        long? actual = f.Field.CanonicalName switch
        {
            "event_id" => e.EventId,
            "device_id" => e.DeviceId,
            _ => null,
        };

        if (actual is null)
        {
            return false; // NULL column: every comparison is unknown → row excluded
        }

        return NumberCompare(f.Operator, actual.Value, long.Parse(f.Value, CultureInfo.InvariantCulture));
    }

    private static bool TimestampField(FieldTermNode f, SyslogEvent e)
    {
        DateTimeOffset? actual = f.Field.CanonicalName == "event_time" ? e.EventUtc : e.ReceivedUtc;
        if (actual is null)
        {
            return false;
        }

        var target = DateTimeOffset.Parse(f.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return Compare(f.Operator, actual.Value.CompareTo(target));
    }

    private static bool ReferenceField(FieldTermNode f, QueryEvaluationContext ctx)
    {
        if (f.Field.CanonicalName == "device")
        {
            return StringColumn(f, ctx.DeviceName);
        }

        // stream — membership test; NOT EXISTS also matches an event with no streams.
        bool anyEquals = ctx.StreamNames.Any(s => string.Equals(s, f.Value, IC));
        if (f.Operator == ComparisonOperator.NotEquals)
        {
            return !anyEquals;
        }

        return f.Prefix
            ? ctx.StreamNames.Any(s => s.StartsWith(f.Value, IC))
            : anyEquals;
    }

    private static bool CustomField(FieldTermNode f, SyslogEvent e)
    {
        string name = f.Field.CustomFieldName!;
        List<string> values = e.Fields
            .Where(x => string.Equals(x.Name, name, IC))
            .Select(x => x.Value)
            .ToList();

        if (f.Operator == ComparisonOperator.NotEquals)
        {
            return !values.Any(v => string.Equals(v, f.Value, IC));
        }

        if (f.Operator == ComparisonOperator.Equals)
        {
            return values.Any(v => f.Prefix ? v.StartsWith(f.Value, IC) : string.Equals(v, f.Value, IC));
        }

        // range: numeric cast, mirrors CAST(value AS REAL) in the compiled SQL
        if (!double.TryParse(f.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
        {
            return false;
        }

        return values.Any(v =>
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) &&
            Compare(f.Operator, d.CompareTo(target)));
    }

    // --------------------------------------------------------------- helpers

    private static bool NumberCompare(ComparisonOperator op, long actual, long target) =>
        Compare(op, actual.CompareTo(target));

    private static bool Compare(ComparisonOperator op, int cmp) => op switch
    {
        ComparisonOperator.Equals => cmp == 0,
        ComparisonOperator.NotEquals => cmp != 0,
        ComparisonOperator.GreaterThan => cmp > 0,
        ComparisonOperator.GreaterThanOrEqual => cmp >= 0,
        ComparisonOperator.LessThan => cmp < 0,
        ComparisonOperator.LessThanOrEqual => cmp <= 0,
        _ => false,
    };

    private static string ProtocolToken(Protocol p) => p switch
    {
        Protocol.Udp => "udp",
        Protocol.Tcp => "tcp",
        Protocol.Tls => "tls",
        Protocol.Snmp => "snmp",
        Protocol.WinEventLog => "wineventlog",
        _ => p.ToString().ToLowerInvariant(),
    };

    private static string ParseStatusToken(ParseStatus s) => s switch
    {
        ParseStatus.Raw => "raw",
        ParseStatus.Rfc3164 => "rfc3164",
        ParseStatus.Rfc5424 => "rfc5424",
        _ => s.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// The extra facts <see cref="QueryEvaluator"/> needs that are not on the event itself:
/// the names of the device and streams it belongs to, so <c>device:core-sw1</c> and
/// <c>stream:Firewall</c> terms can be evaluated by name.
/// </summary>
public sealed record QueryEvaluationContext
{
    public static QueryEvaluationContext Empty { get; } = new();

    /// <summary>The event's device name, if it has a matched device.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Names of every stream the event is a member of.</summary>
    public IReadOnlyCollection<string> StreamNames { get; init; } = [];
}
