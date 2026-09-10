using System.Globalization;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Rules.Alerts;

/// <summary>
/// Projects an event to the group key an alert counts by. Must agree exactly with the SQL
/// <c>GROUP BY</c> expression in <c>SqliteAlertWindowReader</c> — the differential test
/// (SQL aggregate vs in-memory grouping) proves the two evaluation paths cannot diverge.
/// The SQL path uses <c>COALESCE(column, '(none)')</c>, so a null column value groups as
/// <c>(none)</c> here too.
/// </summary>
public static class AlertGrouping
{
    /// <summary>The key an ungrouped alert would produce for a null column in SQL.</summary>
    public const string NoneKey = "(none)";

    /// <summary>The group key for one event, or null when the alert is ungrouped.</summary>
    public static string? KeyFor(string? groupByField, SyslogEvent syslogEvent)
    {
        if (groupByField is null)
        {
            return null;
        }

        if (groupByField.StartsWith(ConditionFields.ExtractedFieldPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = groupByField[ConditionFields.ExtractedFieldPrefix.Length..];
            foreach (EventField f in syslogEvent.Fields)
            {
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return f.Value;
                }
            }

            return NoneKey;
        }

        string? raw = groupByField switch
        {
            "hostname" => syslogEvent.Hostname,
            "source_ip" => syslogEvent.SourceIp,
            "app" => syslogEvent.AppName,
            "proc_id" => syslogEvent.ProcId,
            "msg_id" => syslogEvent.MsgId,
            "vendor" => syslogEvent.Vendor,
            "severity" => ((int)syslogEvent.Severity).ToString(CultureInfo.InvariantCulture),
            "facility" => ((int)syslogEvent.Facility).ToString(CultureInfo.InvariantCulture),
            "protocol" => ProtocolToken(syslogEvent.Protocol),
            "parse_status" => ParseStatusToken(syslogEvent.ParseStatus),
            _ => null,
        };

        return raw ?? NoneKey;
    }

    /// <summary>The distinct-count key: the raw value, or null when the event has no such value.</summary>
    public static string? DistinctKeyFor(string groupByField, SyslogEvent syslogEvent)
    {
        string? key = KeyFor(groupByField, syslogEvent);
        return key == NoneKey ? null : key;
    }

    private static string ProtocolToken(Core.Enums.Protocol p) => p switch
    {
        Core.Enums.Protocol.Udp => "udp",
        Core.Enums.Protocol.Tcp => "tcp",
        Core.Enums.Protocol.Tls => "tls",
        Core.Enums.Protocol.Snmp => "snmp",
        Core.Enums.Protocol.WinEventLog => "wineventlog",
        _ => p.ToString().ToLowerInvariant(),
    };

    private static string ParseStatusToken(Core.Enums.ParseStatus s) => s switch
    {
        Core.Enums.ParseStatus.Raw => "raw",
        Core.Enums.ParseStatus.Rfc3164 => "rfc3164",
        Core.Enums.ParseStatus.Rfc5424 => "rfc5424",
        _ => s.ToString().ToLowerInvariant(),
    };
}
