namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>Tuning for the collector-health self-monitoring tick (PHASE_11 items 5-7).</summary>
public sealed class SelfMonitoringOptions
{
    public const string SectionName = "SelfMonitoring";

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);

    public long DiskFreeBytesMinimum { get; set; } = 5_000_000_000; // 5 GB

    public int QueueDepthPercentMax { get; set; } = 80;

    public TimeSpan QueueDepthSustainedFor { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How far back to look for a newly-failed archive verification each tick —
    /// must be at least <see cref="TickInterval"/> so a failure between ticks is never missed.</summary>
    public TimeSpan ArchiveVerificationLookback { get; set; } = TimeSpan.FromMinutes(5);
}
