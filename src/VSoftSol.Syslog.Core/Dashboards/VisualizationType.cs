namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// How a widget draws its aggregation result (PHASE_09 build item 3). Every type consumes
/// the same <see cref="AggregationResult"/> shape — one render path, no per-widget code.
/// </summary>
public enum VisualizationType
{
    /// <summary>Time-bucketed series as a line.</summary>
    TimeSeriesLine,

    /// <summary>Time-bucketed series as a filled area.</summary>
    TimeSeriesArea,

    /// <summary>One bar per group value, largest first.</summary>
    Bar,

    /// <summary>A single headline number with an optional sparkline.</summary>
    Counter,

    /// <summary>The top N group values in a compact table.</summary>
    TopNTable,

    /// <summary>Event count by severity as a donut.</summary>
    SeverityDonut,

    /// <summary>A single rate (events/sec or a metric) against a soft ceiling.</summary>
    RateGauge,

    /// <summary>The most recent matching events, newest first.</summary>
    RecentEventsTable,

    /// <summary>One tile per in-scope device, coloured by how recently it was heard from.</summary>
    DeviceStatusGrid,
}
