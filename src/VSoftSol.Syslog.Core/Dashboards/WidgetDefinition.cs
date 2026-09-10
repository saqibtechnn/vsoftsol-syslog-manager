namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// One widget on a dashboard (PHASE_09 build item 1): a stable id, a title, a data source,
/// an aggregation, a visualization, and optional per-widget overrides for the time range
/// and refresh interval (null ⇒ inherit the dashboard's — build items 5 and 4). Stored as
/// part of the dashboard's <c>widgets_json</c>; there is no per-widget table.
/// </summary>
public sealed record WidgetDefinition
{
    /// <summary>Stable identifier, unique within the dashboard. Survives edits and reordering.</summary>
    public required string Id { get; init; }

    /// <summary>The heading shown on the widget. Rendered as text — never as markup.</summary>
    public string Title { get; init; } = string.Empty;

    public required WidgetSource Source { get; init; }

    public AggregationSpec Aggregation { get; init; } = new();

    public VisualizationType Visualization { get; init; } = VisualizationType.Counter;

    /// <summary>
    /// A per-widget time window that overrides the dashboard default. Null ⇒ inherit
    /// (PHASE_09 build item 5).
    /// </summary>
    public TimeSpan? TimeRangeOverride { get; init; }

    /// <summary>
    /// A per-widget auto-refresh interval that overrides the dashboard default. Null ⇒
    /// inherit; <see cref="TimeSpan.Zero"/> ⇒ never auto-refresh.
    /// </summary>
    public TimeSpan? RefreshOverride { get; init; }

    /// <summary>Creates a widget with a fresh id.</summary>
    public static WidgetDefinition Create(
        string title,
        WidgetSource source,
        AggregationSpec aggregation,
        VisualizationType visualization) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Source = source,
            Aggregation = aggregation,
            Visualization = visualization,
        };
}
