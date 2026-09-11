namespace VSoftSol.Syslog.Core.Retention;

/// <summary>
/// The single global retention configuration row (PHASE_10 build item 1) — defaults for
/// any stream without its own <see cref="RetentionPolicy"/>, plus the archive root and
/// tiering batch size shared by every stream.
/// </summary>
public sealed record RetentionSettings
{
    public int DefaultHotDays { get; init; } = 30;

    public int DefaultWarmDays { get; init; } = 90;

    public int DefaultColdDays { get; init; } = 365;

    /// <summary>Local directory or UNC path archive files are written under when a stream
    /// has no <see cref="RetentionPolicy.ArchivePath"/> override. Empty resolves to a
    /// subfolder of the data directory at runtime.</summary>
    public string ArchiveRoot { get; init; } = string.Empty;

    public int CompressionLevel { get; init; } = 3;

    /// <summary>Events (or archives) processed per tiering batch — bounds writer-lock time.</summary>
    public int BatchSize { get; init; } = 500;

    public DateTimeOffset? UpdatedUtc { get; init; }

    public string? UpdatedBy { get; init; }
}
