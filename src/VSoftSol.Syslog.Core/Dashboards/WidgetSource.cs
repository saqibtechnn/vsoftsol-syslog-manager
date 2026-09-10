namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>Which data a widget aggregates.</summary>
public enum WidgetSourceKind
{
    /// <summary>Events, selected by a saved search or an inline Phase 5 query.</summary>
    EventQuery,

    /// <summary>A single collector-health metric from the sampler table.</summary>
    SystemSeries,
}

/// <summary>
/// A widget's data source (PHASE_09 build item 1). Exactly one path: an
/// <see cref="WidgetSourceKind.EventQuery"/> carries a saved-search id <em>or</em> an inline
/// query string (both null ⇒ "everything in the window"); a
/// <see cref="WidgetSourceKind.SystemSeries"/> carries a <see cref="SystemMetric"/>. Every
/// event query is run under the viewer's scope, so a shared dashboard shows each viewer
/// their own data.
/// </summary>
public sealed record WidgetSource
{
    public WidgetSourceKind Kind { get; init; } = WidgetSourceKind.EventQuery;

    /// <summary>The saved search backing this widget, or null for an inline / match-all query.</summary>
    public long? SavedSearchId { get; init; }

    /// <summary>A Phase 5 query string, used when <see cref="SavedSearchId"/> is null. Empty ⇒ match-all.</summary>
    public string? InlineQuery { get; init; }

    /// <summary>The metric, when <see cref="Kind"/> is <see cref="WidgetSourceKind.SystemSeries"/>.</summary>
    public SystemMetric? Metric { get; init; }

    public static WidgetSource MatchAll { get; } = new() { Kind = WidgetSourceKind.EventQuery, InlineQuery = string.Empty };

    public static WidgetSource Query(string query) =>
        new() { Kind = WidgetSourceKind.EventQuery, InlineQuery = query };

    public static WidgetSource Saved(long savedSearchId) =>
        new() { Kind = WidgetSourceKind.EventQuery, SavedSearchId = savedSearchId };

    public static WidgetSource System(SystemMetric metric) =>
        new() { Kind = WidgetSourceKind.SystemSeries, Metric = metric };
}
