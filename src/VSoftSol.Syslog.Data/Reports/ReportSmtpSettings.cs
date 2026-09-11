namespace VSoftSol.Syslog.Data.Reports;

/// <summary>The single global SMTP profile scheduled reports deliver through
/// (PHASE_10 build item 8) — mirrors <c>discovery_settings</c>.</summary>
public sealed record ReportSmtpSettings
{
    public string? Host { get; init; }

    public int Port { get; init; } = 587;

    public string? FromAddress { get; init; }

    public string? Username { get; init; }

    /// <summary>Name of the DPAPI-protected secret holding the SMTP password, or null for
    /// unauthenticated relay.</summary>
    public string? SecretName { get; init; }

    public bool UseTls { get; init; } = true;

    public DateTimeOffset? UpdatedUtc { get; init; }

    public string? UpdatedBy { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
