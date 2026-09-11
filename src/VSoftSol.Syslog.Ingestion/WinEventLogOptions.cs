using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Windows Event Log intake endpoint tuning (PHASE_11 item 3). Off by default and bound to
/// loopback by default — a forwarder running on the collector box itself (or reachable via
/// an explicit operator-configured bind address/port forward) posts here, authenticated by
/// an API key (Settings → Integrations), never by network location alone.
/// </summary>
public sealed class WinEventLogOptions
{
    public const string SectionName = "WinEventLog";

    public bool Enabled { get; set; }

    [Required]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int Port { get; set; } = 8514;

    [Range(1, 100_000)]
    public int MaxRequestsPerSourcePerMinute { get; set; } = 600;

    [Range(1024, 16 * 1024 * 1024)]
    public int MaxBodyBytes { get; set; } = 256 * 1024;
}
