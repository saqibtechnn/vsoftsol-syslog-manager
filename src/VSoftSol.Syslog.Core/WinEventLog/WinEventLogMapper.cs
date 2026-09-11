using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.WinEventLog;

/// <summary>
/// Maps a Windows Event Log entry's <c>Level</c> and <c>Channel</c> into the canonical
/// syslog severity/facility so a forwarded Windows event lands in search, rules, and
/// dashboards exactly like a syslog message (PHASE_11 item 3: "normalize them into the
/// same schema, mapping level to severity and channel to facility").
/// </summary>
public static class WinEventLogMapper
{
    /// <summary>Windows Event Log levels (System.Diagnostics.Eventing.Reader.StandardEventLevel):
    /// 0 = LogAlways, 1 = Critical, 2 = Error, 3 = Warning, 4 = Information, 5 = Verbose.</summary>
    public static Severity SeverityFor(int level) => level switch
    {
        1 => Severity.Critical,
        2 => Severity.Error,
        3 => Severity.Warning,
        4 => Severity.Informational,
        5 => Severity.Debug,
        _ => Severity.Informational, // 0 (LogAlways) and any unrecognised value
    };

    /// <summary>The handful of well-known channels get a specific facility; every custom
    /// application/service channel (the overwhelming majority — "Microsoft-Windows-...",
    /// third-party app channels) falls back to Local7, the same "unrecognised source, still
    /// collected" convention Tier 1 already uses for unknown syslog vendors.</summary>
    public static Facility FacilityFor(string channel) => channel switch
    {
        "Security" => Facility.SecurityAuth,
        "System" => Facility.Syslogd,
        "Application" => Facility.User,
        _ => Facility.Local7,
    };
}
