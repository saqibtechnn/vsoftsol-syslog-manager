namespace VSoftSol.Syslog.Core.Snmp;

/// <summary>SNMP protocol version carried in the message header.</summary>
public enum SnmpVersion
{
    V1 = 0,
    V2c = 1,
}

/// <summary>How a varbind's value was encoded on the wire (PHASE_11: "MIB-free varbind
/// capture" — the kind is recorded for display; no MIB is consulted or required).</summary>
#pragma warning disable CA1720 // these are the actual ASN.1/SNMP wire type names (INTEGER, OCTET STRING, ...); renaming them would hurt clarity, not help it.
public enum SnmpVarbindKind
{
    Integer,
    OctetString,
    Null,
    ObjectIdentifier,
    IpAddress,
    Counter32,
    Gauge32,
    TimeTicks,
    Counter64,
    Other,
}
#pragma warning restore CA1720

/// <summary>One name/value pair from a trap's variable-bindings list.</summary>
public sealed record SnmpVarbind(string Oid, SnmpVarbindKind Kind, string DisplayValue);

/// <summary>
/// A decoded SNMPv1 or SNMPv2c trap. SNMPv1 carries <see cref="EnterpriseOid"/>,
/// <see cref="AgentAddress"/>, <see cref="GenericTrap"/>, and <see cref="SpecificTrap"/>
/// directly in the PDU; SNMPv2c instead carries the trap identity as the
/// <c>snmpTrapOID.0</c> varbind (<see cref="TrapOid"/>) and the uptime as the
/// <c>sysUpTime.0</c> varbind (<see cref="UptimeTicks"/>) — both are still surfaced here,
/// pulled out of the varbind list by <see cref="SnmpBerReader"/> for a uniform shape.
/// </summary>
public sealed record SnmpTrapMessage(
    SnmpVersion Version,
    string Community,
    string? AgentAddress,
    string? EnterpriseOid,
    int? GenericTrap,
    int? SpecificTrap,
    long? UptimeTicks,
    string? TrapOid,
    IReadOnlyList<SnmpVarbind> Varbinds);
