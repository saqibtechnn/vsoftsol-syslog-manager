using VSoftSol.Syslog.Core.Dashboards;

namespace VSoftSol.Syslog.Data.Seed;

/// <summary>
/// The four dashboards the product ships with (PHASE_09 build item 6). Seeded on startup by
/// <see cref="DatabaseSeeder"/>, keyed by <see cref="DashboardDefinition.SystemKey"/> so
/// re-seeding is idempotent. They are <see cref="DashboardDefinition.IsSystem"/> —
/// non-editable and non-deletable, but "Copy to edit" makes an owned clone. Every one must
/// be genuinely useful on day one with zero customization.
/// </summary>
public static class DefaultDashboards
{
    public static IReadOnlyList<DashboardDefinition> All { get; } =
    [
        NetworkOverview(),
        SecurityOverview(),
        DeviceHealth(),
        CollectorHealth(),
    ];

    private static DashboardDefinition NetworkOverview()
    {
        var widgets = new[]
        {
            Widget("net-msgrate", "Message rate", WidgetSource.MatchAll,
                Agg(AggregationFunction.Count, bucket: BucketInterval.Auto), VisualizationType.TimeSeriesArea),
            Widget("net-talkers", "Top talkers", WidgetSource.MatchAll,
                Agg(AggregationFunction.Count, groupBy: "hostname", topN: 10), VisualizationType.Bar),
            Widget("net-severity", "Severity distribution", WidgetSource.MatchAll,
                Agg(AggregationFunction.Count), VisualizationType.SeverityDonut),
            Widget("net-criticals", "Recent criticals", WidgetSource.Query("severity:>=critical"),
                Agg(AggregationFunction.Count), VisualizationType.RecentEventsTable),
        };

        return System("network-overview", "Network Overview",
            "Message volume, the busiest devices, how severe traffic is, and the latest critical events.",
            TimeSpan.FromHours(24), widgets,
            Layout(("net-msgrate", 0, 0, 8, 1), ("net-severity", 8, 0, 4, 1),
                   ("net-talkers", 0, 1, 6, 1), ("net-criticals", 6, 1, 6, 1)));
    }

    private static DashboardDefinition SecurityOverview()
    {
        var widgets = new[]
        {
            Widget("sec-authfail", "Authentication failures by host",
                WidgetSource.Query("(failed OR failure OR invalid OR denied) AND (login OR authentication OR password OR user)"),
                Agg(AggregationFunction.Count, groupBy: "hostname", topN: 10), VisualizationType.Bar),
            Widget("sec-config", "Configuration changes",
                WidgetSource.Query("config OR configured OR \"configuration change\" OR \"%SYS-5-CONFIG\""),
                Agg(AggregationFunction.Count, bucket: BucketInterval.Auto), VisualizationType.TimeSeriesLine),
            Widget("sec-distinct-ip", "Distinct source IPs",
                WidgetSource.MatchAll,
                Agg(AggregationFunction.DistinctCount, valueField: "source_ip"), VisualizationType.Counter),
            Widget("sec-authfail-trend", "Authentication failures over time",
                WidgetSource.Query("(failed OR failure OR invalid OR denied) AND (login OR authentication OR password OR user)"),
                Agg(AggregationFunction.Count, bucket: BucketInterval.Auto), VisualizationType.TimeSeriesArea),
        };

        return System("security-overview", "Security Overview",
            "Failed authentication by source, configuration-change activity, and how many distinct hosts are talking.",
            TimeSpan.FromHours(24), widgets,
            Layout(("sec-authfail", 0, 0, 6, 1), ("sec-distinct-ip", 6, 0, 3, 1), ("sec-config", 9, 0, 3, 1),
                   ("sec-authfail-trend", 0, 1, 12, 1)));
    }

    private static DashboardDefinition DeviceHealth()
    {
        var widgets = new[]
        {
            Widget("dev-grid", "Device status", WidgetSource.MatchAll,
                Agg(AggregationFunction.Count), VisualizationType.DeviceStatusGrid),
            Widget("dev-parsefail", "Unparsed messages by host", WidgetSource.Query("parse_status:raw"),
                Agg(AggregationFunction.Count, groupBy: "hostname", topN: 10), VisualizationType.Bar),
            Widget("dev-parsefail-rate", "Unparsed message rate", WidgetSource.Query("parse_status:raw"),
                Agg(AggregationFunction.Count, bucket: BucketInterval.Auto), VisualizationType.TimeSeriesLine),
        };

        return System("device-health", "Device Health",
            "Which devices are healthy, quiet, or silent, and where messages are failing to parse.",
            TimeSpan.FromHours(24), widgets,
            Layout(("dev-grid", 0, 0, 12, 2),
                   ("dev-parsefail", 0, 2, 6, 1), ("dev-parsefail-rate", 6, 2, 6, 1)));
    }

    private static DashboardDefinition CollectorHealth()
    {
        var widgets = new[]
        {
            Widget("col-rate", "Ingest rate", WidgetSource.System(SystemMetric.IngestRate),
                Agg(AggregationFunction.Count, bucket: BucketInterval.Auto), VisualizationType.TimeSeriesArea),
            Widget("col-queue", "Queue depth", WidgetSource.System(SystemMetric.ChannelDepth),
                Agg(AggregationFunction.Count), VisualizationType.Counter),
            Widget("col-spill", "Spill queue", WidgetSource.System(SystemMetric.SpillBytes),
                Agg(AggregationFunction.Count), VisualizationType.Counter),
            Widget("col-db", "Database size", WidgetSource.System(SystemMetric.DatabaseBytes),
                Agg(AggregationFunction.Count), VisualizationType.Counter),
            Widget("col-disk", "Disk free", WidgetSource.System(SystemMetric.DiskFreeBytes),
                Agg(AggregationFunction.Count), VisualizationType.Counter),
            Widget("col-drops", "Dropped frames", WidgetSource.System(SystemMetric.DropsTotal),
                Agg(AggregationFunction.Count), VisualizationType.Counter),
        };

        return System("collector-health", "Collector Health",
            "The collector's own vital signs: how fast it is ingesting, how deep the queue and spill are, and how much disk is left.",
            TimeSpan.FromHours(4), widgets,
            Layout(("col-rate", 0, 0, 12, 1),
                   ("col-queue", 0, 1, 4, 1), ("col-spill", 4, 1, 4, 1), ("col-db", 8, 1, 4, 1),
                   ("col-disk", 0, 2, 4, 1), ("col-drops", 4, 2, 4, 1)));
    }

    // -------------------------------------------------------------------- helpers

    private static WidgetDefinition Widget(
        string id, string title, WidgetSource source, AggregationSpec aggregation, VisualizationType visualization) =>
        new() { Id = id, Title = title, Source = source, Aggregation = aggregation, Visualization = visualization };

    private static AggregationSpec Agg(
        AggregationFunction function,
        string? valueField = null,
        string? groupBy = null,
        BucketInterval bucket = BucketInterval.None,
        int topN = 10) =>
        new() { Function = function, ValueField = valueField, GroupByField = groupBy, Bucket = bucket, TopN = topN };

    private static IReadOnlyList<WidgetLayout> Layout(params (string Id, int Col, int Row, int ColSpan, int RowSpan)[] cells) =>
        [.. cells.Select(c => new WidgetLayout
        {
            WidgetId = c.Id,
            Column = c.Col,
            Row = c.Row,
            ColumnSpan = c.ColSpan,
            RowSpan = c.RowSpan,
        })];

    private static DashboardDefinition System(
        string key, string name, string description, TimeSpan defaultRange,
        IReadOnlyList<WidgetDefinition> widgets, IReadOnlyList<WidgetLayout> layout) =>
        new()
        {
            Name = name,
            Description = description,
            IsSystem = true,
            IsShared = true,
            SystemKey = key,
            DefaultTimeRange = defaultRange,
            RefreshInterval = TimeSpan.Zero,
            Widgets = widgets,
            Layout = layout,
        };
}
