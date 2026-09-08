using System.Globalization;
using System.Text.RegularExpressions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.UnitTests.Conditions;

/// <summary>
/// A deliberately naive, uncompiled matcher for a <see cref="ConditionNode"/> tree —
/// the independent reference the production <c>ConditionEvaluator</c> / <c>StreamRouter</c>
/// must agree with over thousands of generated cases (PHASE_06 routing oracle).
/// </summary>
internal static class NaiveConditionMatcher
{
    public static bool Matches(ConditionNode? node, SyslogEvent e)
    {
        if (node is null)
        {
            return false;
        }

        return node switch
        {
            ConditionGroup g when g.Children.Count == 0 => false,
            ConditionGroup { Join: ConditionJoin.And } g => g.Children.All(c => Matches(c, e)),
            ConditionGroup g => g.Children.Any(c => Matches(c, e)),
            ConditionComparison c => MatchesComparison(c, e),
            _ => false,
        };
    }

    private static bool MatchesComparison(ConditionComparison c, SyslogEvent e)
    {
        if (!ConditionFields.TryResolve(c.Field, out ConditionFieldInfo? field))
        {
            return false;
        }

        List<string> values = ReadValues(field, e);

        return c.Operator switch
        {
            ConditionOperator.Exists => values.Count > 0,
            ConditionOperator.NotExists => values.Count == 0,
            ConditionOperator.Equals => values.Any(v => Ci(v, c.Value)),
            ConditionOperator.NotEquals => !values.Any(v => Ci(v, c.Value)),
            ConditionOperator.Contains => values.Any(v => v.Contains(c.Value, StringComparison.OrdinalIgnoreCase)),
            ConditionOperator.NotContains => !values.Any(v => v.Contains(c.Value, StringComparison.OrdinalIgnoreCase)),
            ConditionOperator.StartsWith => values.Any(v => v.StartsWith(c.Value, StringComparison.OrdinalIgnoreCase)),
            ConditionOperator.EndsWith => values.Any(v => v.EndsWith(c.Value, StringComparison.OrdinalIgnoreCase)),
            ConditionOperator.InList => values.Any(v => Split(c.Value).Any(item => Ci(v, item))),
            ConditionOperator.GreaterThan => ReadNumeric(field, e) is { } a && double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && a > t,
            ConditionOperator.LessThan => ReadNumeric(field, e) is { } a2 && double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t2) && a2 < t2,
            ConditionOperator.Matches => MatchesRegex(c.Value, values),
            _ => false,
        };
    }

    private static bool MatchesRegex(string pattern, List<string> values)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false; // an unusable pattern matches nothing (and the compiler would have rejected it)
        }

        foreach (string v in values)
        {
            try
            {
                if (regex.IsMatch(v))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // fail closed
            }
        }

        return false;
    }

    private static List<string> ReadValues(ConditionFieldInfo field, SyslogEvent e)
    {
        if (field.Type == ConditionFieldType.ExtractedField)
        {
            string name = field.Name[ConditionFields.ExtractedFieldPrefix.Length..];
            return e.Fields.Where(f => Ci(f.Name, name)).Select(f => f.Value).ToList();
        }

        return field.Name switch
        {
            "message" => Nn(e.Message),
            "hostname" => Nn(e.Hostname),
            "source_ip" => Nn(e.SourceIp),
            "app" => Nn(e.AppName),
            "proc_id" => Nn(e.ProcId),
            "msg_id" => Nn(e.MsgId),
            "vendor" => Nn(e.Vendor),
            "protocol" => Nn(e.Protocol.ToString().ToLowerInvariant()),
            "parse_status" => Nn(e.ParseStatus.ToString().ToLowerInvariant()),
            "occurrence_count" => Nn(e.OccurrenceCount.ToString(CultureInfo.InvariantCulture)),
            "severity" => [((int)e.Severity).ToString(CultureInfo.InvariantCulture), e.Severity.ToString().ToLowerInvariant()],
            "facility" => [((int)e.Facility).ToString(CultureInfo.InvariantCulture), e.Facility.ToString().ToLowerInvariant()],
            _ => [],
        };
    }

    private static double? ReadNumeric(ConditionFieldInfo field, SyslogEvent e) => field.Name switch
    {
        "severity" => (int)e.Severity,
        "facility" => (int)e.Facility,
        "occurrence_count" => e.OccurrenceCount,
        _ => ReadValues(field, e).Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null).FirstOrDefault(d => d is not null),
    };

    private static List<string> Nn(string? v) => v is null ? [] : [v];

    private static bool Ci(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string[] Split(string csv) =>
        csv.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
