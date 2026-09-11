namespace VSoftSol.Syslog.Core.Retention;

/// <summary>
/// A per-stream retention policy (PHASE_10 build item 1). Every duration is how long an
/// event spends in that tier, not a cumulative age — an event moves Hot to Warm at age
/// &gt; <see cref="HotDays"/>, Warm to Cold (archived and removed from <c>events</c>) at
/// age &gt; <see cref="HotDays"/> + <see cref="WarmDays"/>, and its archive is purged at
/// archive age &gt; <see cref="ColdDays"/>. A null <see cref="StreamId"/> is the global
/// default applied to any stream without its own row.
/// </summary>
public sealed record RetentionPolicy
{
    public long? StreamId { get; init; }

    public int HotDays { get; init; } = 30;

    public int WarmDays { get; init; } = 90;

    public int ColdDays { get; init; } = 365;

    /// <summary>Local directory or UNC path archives for this stream are written under.
    /// Null means "use the global <see cref="RetentionSettings.ArchiveRoot"/>".</summary>
    public string? ArchivePath { get; init; }

    /// <summary>Zstd compression level, 1 (fastest) to 19 (smallest).</summary>
    public int CompressionLevel { get; init; } = 3;

    public DateTimeOffset? UpdatedUtc { get; init; }

    public string? UpdatedBy { get; init; }

    /// <summary>Age, in days, at which a Hot event becomes eligible for Warm compression.</summary>
    public int WarmThresholdDays => HotDays;

    /// <summary>Age, in days, at which a Warm event becomes eligible for Cold export.</summary>
    public int ColdThresholdDays => HotDays + WarmDays;
}
