using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// The allow-lists the widget picker offers and the validator enforces (PHASE_09 — widget
/// creation is a guided picker, never a JSON editor). Which event fields can be grouped by,
/// which can be summed / averaged, and which visualizations accept which aggregation
/// shapes. Pure; shared by the Web picker and the store-side validator so both agree.
/// </summary>
public static class DashboardWidgetCatalog
{
    /// <summary>Built-in columns a widget may group by. <c>message</c> is deliberately absent.</summary>
    public static IReadOnlyList<ConditionFieldInfo> GroupableFields { get; } =
    [
        new("hostname", "Hostname", ConditionFieldType.Text),
        new("source_ip", "Source IP", ConditionFieldType.Text),
        new("app", "Application / tag", ConditionFieldType.Text),
        new("proc_id", "Process ID", ConditionFieldType.Text),
        new("msg_id", "Message ID", ConditionFieldType.Text),
        new("severity", "Severity", ConditionFieldType.Severity),
        new("facility", "Facility", ConditionFieldType.Facility),
        new("vendor", "Vendor", ConditionFieldType.Text),
        new("protocol", "Protocol", ConditionFieldType.Token),
        new("parse_status", "Parse status", ConditionFieldType.Token),
    ];

    /// <summary>Built-in numeric columns the arithmetic functions may operate on.</summary>
    public static IReadOnlyList<ConditionFieldInfo> NumericFields { get; } =
    [
        new("severity", "Severity code", ConditionFieldType.Severity),
        new("facility", "Facility code", ConditionFieldType.Facility),
        new("occurrence_count", "Occurrence count", ConditionFieldType.Number),
    ];

    private static readonly HashSet<string> GroupableNames =
        new(GroupableFields.Select(f => f.Name), StringComparer.Ordinal);

    private static readonly HashSet<string> NumericNames =
        new(NumericFields.Select(f => f.Name), StringComparer.Ordinal);

    /// <summary>True when <paramref name="field"/> is a groupable built-in or a well-formed <c>field.&lt;name&gt;</c>.</summary>
    public static bool IsGroupable(string? field) =>
        field is not null
        && (GroupableNames.Contains(field)
            || (ConditionFields.TryResolve(field, out ConditionFieldInfo? info)
                && info.Type == ConditionFieldType.ExtractedField));

    /// <summary>True when <paramref name="field"/> is a numeric built-in or a well-formed <c>field.&lt;name&gt;</c>.</summary>
    public static bool IsNumeric(string? field) =>
        field is not null
        && (NumericNames.Contains(field)
            || (ConditionFields.TryResolve(field, out ConditionFieldInfo? info)
                && info.Type == ConditionFieldType.ExtractedField));

    /// <summary>What a visualization needs from the aggregation.</summary>
    public sealed record VisualizationRule(
        bool RequiresBucket,
        bool RequiresGroupBy,
        bool ForbidsBucket,
        bool ForbidsGroupBy,
        bool IsListWidget);

    private static readonly Dictionary<VisualizationType, VisualizationRule> Rules = new()
    {
        [VisualizationType.TimeSeriesLine] = new(RequiresBucket: true, false, ForbidsBucket: false, false, false),
        [VisualizationType.TimeSeriesArea] = new(RequiresBucket: true, false, ForbidsBucket: false, false, false),
        [VisualizationType.Bar] = new(false, RequiresGroupBy: true, ForbidsBucket: true, false, false),
        [VisualizationType.TopNTable] = new(false, RequiresGroupBy: true, ForbidsBucket: true, false, false),
        [VisualizationType.Counter] = new(false, false, ForbidsBucket: true, ForbidsGroupBy: true, false),
        [VisualizationType.SeverityDonut] = new(false, false, ForbidsBucket: true, false, false),
        [VisualizationType.RateGauge] = new(false, false, ForbidsBucket: true, ForbidsGroupBy: true, false),
        [VisualizationType.RecentEventsTable] = new(false, false, false, false, IsListWidget: true),
        [VisualizationType.DeviceStatusGrid] = new(false, false, false, false, IsListWidget: true),
    };

    public static VisualizationRule RuleFor(VisualizationType visualization) => Rules[visualization];

    /// <summary>
    /// A list widget (<c>RecentEventsTable</c>, <c>DeviceStatusGrid</c>) draws rows, not an
    /// aggregation — its <see cref="AggregationSpec"/> is ignored.
    /// </summary>
    public static bool IsListWidget(VisualizationType visualization) => Rules[visualization].IsListWidget;

    /// <summary>The visualizations that make sense for a <see cref="WidgetSourceKind.SystemSeries"/> source.</summary>
    public static IReadOnlyList<VisualizationType> SystemSeriesVisualizations { get; } =
    [
        VisualizationType.TimeSeriesLine,
        VisualizationType.TimeSeriesArea,
        VisualizationType.Counter,
        VisualizationType.RateGauge,
    ];
}
