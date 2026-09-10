namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>Tunables for the Phase 9 dashboard runtime (<c>Dashboards</c> config section).</summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboards";

    /// <summary>
    /// How long a widget aggregation result is cached (PHASE_09 build item 8 — a shared wall
    /// dashboard must not hammer the database). Keyed by scope + query + range, so a
    /// time-range change bypasses it naturally.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The shortest auto-refresh interval a dashboard may be configured with.</summary>
    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many rows a "recent events" widget shows.</summary>
    public int RecentEventsLimit { get; set; } = 25;
}

/// <summary>Tunables for the collector-health sampler (<c>CollectorStats</c> config section).</summary>
public sealed class CollectorStatOptions
{
    public const string SectionName = "CollectorStats";

    /// <summary>How often a <c>collector_stat_samples</c> row is written.</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Samples older than this are pruned on each tick.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(72);
}
