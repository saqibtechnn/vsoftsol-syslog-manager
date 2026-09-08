using System.Globalization;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Rules.Conditions;

/// <summary>
/// Projects a <see cref="SyslogEvent"/>'s fields to the string values a condition tests.
/// Severity and facility yield both their code and their name so <c>equals error</c> and
/// <c>equals 3</c> both work; an extracted field can yield several values (one per
/// <c>event_fields</c> row with that name).
/// </summary>
internal static class EventFieldReader
{
    public static IReadOnlyList<string> Values(ConditionFieldInfo field, SyslogEvent e)
    {
        if (field.Type == ConditionFieldType.ExtractedField)
        {
            string name = field.Name[ConditionFields.ExtractedFieldPrefix.Length..];
            return e.Fields.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Value)
                .ToArray();
        }

        return field.Name switch
        {
            "message" => One(e.Message),
            "hostname" => One(e.Hostname),
            "source_ip" => One(e.SourceIp),
            "app" => One(e.AppName),
            "proc_id" => One(e.ProcId),
            "msg_id" => One(e.MsgId),
            "vendor" => One(e.Vendor),
            "protocol" => One(ProtocolToken(e.Protocol)),
            "parse_status" => One(ParseStatusToken(e.ParseStatus)),
            "occurrence_count" => One(e.OccurrenceCount.ToString(CultureInfo.InvariantCulture)),
            "severity" => [((int)e.Severity).ToString(CultureInfo.InvariantCulture), e.Severity.ToString().ToLowerInvariant()],
            "facility" => [((int)e.Facility).ToString(CultureInfo.InvariantCulture), e.Facility.ToString().ToLowerInvariant()],
            _ => [],
        };
    }

    /// <summary>The single numeric value for <c>&gt;</c> / <c>&lt;</c>, if the field has one.</summary>
    public static double? Numeric(ConditionFieldInfo field, SyslogEvent e) => field.Name switch
    {
        "severity" => (int)e.Severity,
        "facility" => (int)e.Facility,
        "occurrence_count" => e.OccurrenceCount,
        _ => Values(field, e)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null)
            .FirstOrDefault(d => d is not null),
    };

    private static IReadOnlyList<string> One(string? value) => value is null ? [] : [value];

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
