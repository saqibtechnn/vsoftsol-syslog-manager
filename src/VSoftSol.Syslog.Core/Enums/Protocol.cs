namespace VSoftSol.Syslog.Core.Enums;

/// <summary>
/// Transport a message was received on. Persisted as the lower-case token in the
/// <c>protocol</c> column (see BUILD_PLAN.md canonical schema).
/// </summary>
public enum Protocol
{
    Udp = 0,
    Tcp = 1,
    Tls = 2,
    Snmp = 3,
    WinEventLog = 4,
}
