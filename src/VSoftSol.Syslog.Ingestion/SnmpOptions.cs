using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// SNMP trap receiver tuning (PHASE_11 item 2). Off by default, and even when
/// <see cref="Enabled"/> the listener refuses to start until a real community string is
/// configured — <c>public</c> is never accepted as the configured value (SECURITY_STANDARDS.md:
/// "the default configuration is not <c>public</c>").
/// </summary>
public sealed class SnmpOptions
{
    public const string SectionName = "Snmp";

    public bool Enabled { get; set; }

    [Required]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Range(1, 65535)]
    public int Port { get; set; } = 162;

    /// <summary>Name of the DPAPI-protected secret holding the accepted community
    /// string(s) (comma-separated for more than one). Never logged, never returned from
    /// any admin API — only compared against.</summary>
    public string? CommunitySecretName { get; set; }

    public int MaxVarbindsPerTrap { get; set; } = 256;

    public int MaxOidArcs { get; set; } = 128;
}
