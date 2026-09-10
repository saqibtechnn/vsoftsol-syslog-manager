namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// The bounded allow-list of collector-health values a <see cref="WidgetSource"/> of kind
/// <see cref="WidgetSourceKind.SystemSeries"/> can chart (PHASE_09 build item 6, the
/// Collector Health dashboard). Each is one numeric column of the
/// <c>collector_stat_samples</c> table written by the collector-host sampler; they are read
/// through the same aggregation / visualization path as event widgets, never bespoke code.
/// These are process-wide operational counters, not per-tenant data, so no scope filter
/// applies.
/// </summary>
public enum SystemMetric
{
    /// <summary>Events committed per second, derived from consecutive samples.</summary>
    IngestRate,

    /// <summary>In-memory ingestion channel depth.</summary>
    ChannelDepth,

    /// <summary>Frames currently held in the disk spill queue.</summary>
    SpillFrames,

    /// <summary>Bytes currently in the disk spill queue.</summary>
    SpillBytes,

    /// <summary>Size of the SQLite database file, bytes.</summary>
    DatabaseBytes,

    /// <summary>Free space on the data drive, bytes.</summary>
    DiskFreeBytes,

    /// <summary>Cumulative dropped-frame count (all discard reasons).</summary>
    DropsTotal,

    /// <summary>Open TCP connections.</summary>
    ActiveConnections,

    /// <summary>Sources currently quarantined by the rate limiter.</summary>
    QuarantinedSources,
}

/// <summary>Metadata for one <see cref="SystemMetric"/> — label, unit, and whether higher is worse.</summary>
public sealed record SystemMetricInfo(SystemMetric Metric, string Label, string Unit, bool Cumulative);

/// <summary>The <see cref="SystemMetricInfo"/> catalogue, in display order.</summary>
public static class SystemMetrics
{
    public static IReadOnlyList<SystemMetricInfo> All { get; } =
    [
        new(SystemMetric.IngestRate, "Ingest rate", "events/sec", Cumulative: false),
        new(SystemMetric.ChannelDepth, "Queue depth", "frames", Cumulative: false),
        new(SystemMetric.SpillFrames, "Spill queue", "frames", Cumulative: false),
        new(SystemMetric.SpillBytes, "Spill size", "bytes", Cumulative: false),
        new(SystemMetric.DatabaseBytes, "Database size", "bytes", Cumulative: false),
        new(SystemMetric.DiskFreeBytes, "Disk free", "bytes", Cumulative: false),
        new(SystemMetric.DropsTotal, "Dropped frames", "frames", Cumulative: true),
        new(SystemMetric.ActiveConnections, "TCP connections", "connections", Cumulative: false),
        new(SystemMetric.QuarantinedSources, "Quarantined sources", "sources", Cumulative: false),
    ];

    private static readonly Dictionary<SystemMetric, SystemMetricInfo> ByMetric =
        All.ToDictionary(m => m.Metric);

    public static SystemMetricInfo Info(SystemMetric metric) => ByMetric[metric];
}
