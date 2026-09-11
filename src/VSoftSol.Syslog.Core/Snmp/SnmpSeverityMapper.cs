using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.Snmp;

/// <summary>
/// Maps an SNMPv1/v2c generic-trap type (RFC 1157 §6, the seven MIB-free trap categories
/// every agent uses regardless of vendor) to a syslog severity and a stable name, so a trap
/// lands in the canonical event schema exactly like a parsed syslog message.
/// </summary>
public static class SnmpSeverityMapper
{
    public const int ColdStart = 0;
    public const int WarmStart = 1;
    public const int LinkDown = 2;
    public const int LinkUp = 3;
    public const int AuthenticationFailure = 4;
    public const int EgpNeighborLoss = 5;
    public const int EnterpriseSpecific = 6;

    /// <summary>SNMPv2c traps carry their identity as the <c>snmpTrapOID.0</c> varbind, not
    /// a generic-trap integer, so there is nothing to map — <see cref="Severity.Notice"/> is
    /// the deliberately neutral default (MIB-free: this reader cannot know what the OID
    /// means without the vendor's MIB, which Tier 3 explicitly does not require).</summary>
    public static Severity SeverityFor(int? genericTrap) => genericTrap switch
    {
        LinkDown => Severity.Error,
        AuthenticationFailure => Severity.Warning,
        EgpNeighborLoss => Severity.Warning,
        ColdStart or WarmStart or LinkUp => Severity.Informational,
        _ => Severity.Notice,
    };

    public static string NameFor(int? genericTrap) => genericTrap switch
    {
        ColdStart => "coldStart",
        WarmStart => "warmStart",
        LinkDown => "linkDown",
        LinkUp => "linkUp",
        AuthenticationFailure => "authenticationFailure",
        EgpNeighborLoss => "egpNeighborLoss",
        EnterpriseSpecific => "enterpriseSpecific",
        _ => "unknown",
    };
}
