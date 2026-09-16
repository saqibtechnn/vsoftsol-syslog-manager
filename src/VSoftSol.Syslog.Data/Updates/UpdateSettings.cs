namespace VSoftSol.Syslog.Data.Updates;

/// <summary>The single global self-update check state (v1.1 — ADR 0021) — mirrors
/// <c>ReportSmtpSettings</c>. Off by default.</summary>
public sealed record UpdateSettings
{
    public bool CheckEnabled { get; init; }

    public int CheckIntervalHours { get; init; } = 24;

    public DateTimeOffset? LastCheckedUtc { get; init; }

    public string? LastCheckError { get; init; }

    public string? LatestKnownVersion { get; init; }

    /// <summary>The verified <c>UpdateManifest</c>'s canonical JSON, cached for display.</summary>
    public string? LatestManifestJson { get; init; }

    /// <summary>Local path to the verified, staged MSI once a newer version has been
    /// downloaded and hash-checked — null until then.</summary>
    public string? DownloadedMsiPath { get; init; }

    public string? DownloadedMsiSha256 { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }

    public string? UpdatedBy { get; init; }

    public bool UpdateReady => !string.IsNullOrWhiteSpace(DownloadedMsiPath);
}
