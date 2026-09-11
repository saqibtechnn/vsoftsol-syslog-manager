namespace VSoftSol.Syslog.Core.WinEventLog;

/// <summary>
/// One forwarded Windows Event Log record, as posted to the intake endpoint (PHASE_11 item
/// 3). Field names mirror the Windows Event Log schema so a forwarder (a small agent, or
/// PowerShell's <c>Get-WinEvent</c> piped through a script) can build the JSON body
/// directly from <c>System.Diagnostics.Eventing.Reader.EventRecord</c> without translation.
/// </summary>
public sealed record WinEventLogEntry(
    string Computer,
    string Channel,
    string ProviderName,
    int EventId,
    int Level,
    DateTimeOffset TimeCreated,
    string Message,
    IReadOnlyDictionary<string, string>? Data);
