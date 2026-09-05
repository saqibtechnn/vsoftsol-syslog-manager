namespace VSoftSol.Syslog.Core.Enums;

/// <summary>
/// Syslog severity code (RFC 5424 §6.2.1), ordered most-severe (0) to least-severe (7).
/// </summary>
public enum Severity
{
    Emergency = 0,
    Alert = 1,
    Critical = 2,
    Error = 3,
    Warning = 4,
    Notice = 5,
    Informational = 6,
    Debug = 7,
}
