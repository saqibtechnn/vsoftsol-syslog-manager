namespace VSoftSol.Syslog.Core.Enums;

/// <summary>
/// Syslog facility code (RFC 5424 §6.2.1). The numeric value is the wire value:
/// <c>priority = facility * 8 + severity</c>.
/// </summary>
public enum Facility
{
    Kernel = 0,
    User = 1,
    Mail = 2,
    System = 3,
    Security = 4,
    Syslogd = 5,
    LinePrinter = 6,
    News = 7,
    Uucp = 8,
    Clock = 9,
    SecurityAuth = 10,
    Ftp = 11,
    Ntp = 12,
    LogAudit = 13,
    LogAlert = 14,
    ClockDaemon = 15,
    Local0 = 16,
    Local1 = 17,
    Local2 = 18,
    Local3 = 19,
    Local4 = 20,
    Local5 = 21,
    Local6 = 22,
    Local7 = 23,
}
