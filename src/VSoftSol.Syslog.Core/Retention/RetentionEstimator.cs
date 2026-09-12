namespace VSoftSol.Syslog.Core.Retention;

/// <summary>Projected disk usage for a policy (UX_STANDARDS.md: "never let a user configure
/// retention blind" — PHASE_10 UX gate requires this shown inline before saving).</summary>
public sealed record RetentionEstimate
{
    public long HotBytes { get; init; }

    public long WarmBytes { get; init; }

    public long ArchiveBytes { get; init; }

    /// <summary>Hot + Warm — what stays in the live database.</summary>
    public long DatabaseBytes => HotBytes + WarmBytes;

    /// <summary>Database-resident + archive-resident — everything the policy keeps on disk.</summary>
    public long TotalBytes => DatabaseBytes + ArchiveBytes;
}

/// <summary>
/// Pure projection math (PHASE_10 build item 1 / UX gate). Takes measured inputs (events/day,
/// average event size, the compression ratios actually observed on this database) rather
/// than assuming one — a real device mix varies too widely for a hardcoded constant to be
/// honest.
/// </summary>
public static class RetentionEstimator
{
    /// <summary>Default assumed compression ratio when no real sample exists yet (a brand
    /// new database) — Zstd level 3 on syslog text typically lands in this range.</summary>
    public const double DefaultCompressionRatio = 0.35;

    /// <summary>Default assumed average event size (message + raw_message) when no real hot
    /// data exists yet — a reasonable syslog-message default. Shared by
    /// <see cref="VSoftSol.Syslog.Data.Retention.RetentionEstimateReader"/> (brand-new
    /// database) and <see cref="RetentionPresets"/> (first-run wizard, before any events
    /// have ever been ingested).</summary>
    public const double DefaultAvgEventBytes = 300;

    public static RetentionEstimate Estimate(
        RetentionPolicy policy, double eventsPerDay, double avgHotEventBytes, double compressionRatio)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (eventsPerDay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventsPerDay), "Events/day cannot be negative.");
        }

        if (avgHotEventBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(avgHotEventBytes), "Average event size cannot be negative.");
        }

        double ratio = compressionRatio is > 0 and <= 1 ? compressionRatio : DefaultCompressionRatio;

        double hot = eventsPerDay * policy.HotDays * avgHotEventBytes;
        double warm = eventsPerDay * policy.WarmDays * avgHotEventBytes * ratio;
        double archive = eventsPerDay * policy.ColdDays * avgHotEventBytes * ratio;

        return new RetentionEstimate
        {
            HotBytes = (long)Math.Round(hot, MidpointRounding.AwayFromZero),
            WarmBytes = (long)Math.Round(warm, MidpointRounding.AwayFromZero),
            ArchiveBytes = (long)Math.Round(archive, MidpointRounding.AwayFromZero),
        };
    }
}
