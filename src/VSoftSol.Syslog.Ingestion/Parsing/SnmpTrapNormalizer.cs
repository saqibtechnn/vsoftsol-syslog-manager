using System.Globalization;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Snmp;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// Turns a decoded SNMP trap into the same <see cref="SyslogParseResult"/> +
/// <see cref="EventField"/> shape every other source produces, so a trap is searchable,
/// alertable, and dashboardable exactly like a parsed syslog message. Reported as
/// <see cref="ParseStatus.Raw"/> — it is not RFC 3164/5424 syslog grammar — with
/// <c>protocol = snmp</c> (already part of the canonical schema) as the real
/// disambiguator; unlike a genuinely-unparsed message, <see cref="MessageParser"/> still
/// populates <c>message</c> for this protocol (see its remarks).
/// </summary>
public static class SnmpTrapNormalizer
{
    private const int MaxVarbindsSummarized = 10;

    public static (SyslogParseResult Result, IReadOnlyList<EventField> Fields) Normalize(
        ReadOnlySpan<byte> payload, int maxFields, int maxValueLength)
    {
        if (!SnmpBerReader.TryParse(payload, out SnmpTrapMessage? msg, out string? error))
        {
            return (SyslogParseResult.Raw($"(unparseable SNMP trap: {error})"), []);
        }

        bool isV1 = msg!.Version == SnmpVersion.V1;
        string trapName = isV1 ? SnmpSeverityMapper.NameFor(msg.GenericTrap) : msg.TrapOid ?? "unknown";
        Severity severity = isV1 ? SnmpSeverityMapper.SeverityFor(msg.GenericTrap) : Severity.Notice;
        string agent = msg.AgentAddress ?? "(unknown agent)";

        string varbindSummary = string.Join("; ",
            msg.Varbinds.Take(MaxVarbindsSummarized).Select(v => $"{v.Oid}={v.DisplayValue}"));
        string message = $"SNMP {(isV1 ? "v1" : "v2c")} trap {trapName} from {agent}" +
                          (varbindSummary.Length > 0 ? $": {varbindSummary}" : string.Empty);

        var result = new SyslogParseResult
        {
            Status = ParseStatus.Raw,
            Facility = Facility.Local7,
            Severity = severity,
            Hostname = agent,
            AppName = "snmptrap",
            Message = message,
        };

        var fields = new List<EventField>();

        void Add(string name, string? value)
        {
            if (string.IsNullOrEmpty(value) || fields.Count >= maxFields)
            {
                return;
            }

            fields.Add(new EventField(name, value.Length > maxValueLength ? value[..maxValueLength] : value));
        }

        Add("snmp_version", isV1 ? "1" : "2c");
        Add("snmp_trap_name", trapName);
        Add("snmp_enterprise_oid", msg.EnterpriseOid);
        Add("snmp_agent_address", msg.AgentAddress);
        Add("snmp_uptime_ticks", msg.UptimeTicks?.ToString(CultureInfo.InvariantCulture));
        Add("snmp_trap_oid", msg.TrapOid);

        foreach (SnmpVarbind vb in msg.Varbinds)
        {
            if (fields.Count >= maxFields)
            {
                break;
            }

            Add($"snmp_varbind.{vb.Oid}", vb.DisplayValue);
        }

        return (result, fields);
    }
}
