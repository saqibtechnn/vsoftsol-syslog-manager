using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>The outcome of validating a widget or a dashboard: ok, or a list of plain-English problems.</summary>
public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);

    public static ValidationResult Fail(params string[] errors) => new(false, errors);

    public static ValidationResult From(List<string> errors) =>
        errors.Count == 0 ? Success : new ValidationResult(false, errors);
}

/// <summary>
/// Checks that a <see cref="WidgetDefinition"/> is coherent before it is stored
/// (PHASE_09 — the picker cannot produce an invalid widget, and a hand-crafted JSON import
/// is rejected). Pure; no I/O — the saved-search id is checked for existence at the service
/// layer, not here.
/// </summary>
public static class WidgetValidator
{
    /// <summary>Hard ceiling on <see cref="AggregationSpec.TopN"/>.</summary>
    public const int MaxTopN = 50;

    public static ValidationResult Validate(WidgetDefinition widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(widget.Id))
        {
            errors.Add("The widget is missing its identifier.");
        }

        if (widget.Title.Length > 120)
        {
            errors.Add("The widget title must be 120 characters or fewer.");
        }

        if (widget.TimeRangeOverride is { } tr && (tr <= TimeSpan.Zero || tr > TimeSpan.FromDays(366)))
        {
            errors.Add("The widget time range must be between 1 minute and 366 days.");
        }

        if (widget.RefreshOverride is { } rr && rr < TimeSpan.Zero)
        {
            errors.Add("The widget refresh interval cannot be negative.");
        }

        ValidateSource(widget, errors);

        if (!DashboardWidgetCatalog.IsListWidget(widget.Visualization))
        {
            ValidateAggregation(widget, errors);
        }

        return ValidationResult.From(errors);
    }

    private static void ValidateSource(WidgetDefinition widget, List<string> errors)
    {
        WidgetSource s = widget.Source;
        switch (s.Kind)
        {
            case WidgetSourceKind.EventQuery:
                if (s.Metric is not null)
                {
                    errors.Add("An event-query widget cannot also carry a system metric.");
                }

                if (s.SavedSearchId is { } id and <= 0)
                {
                    errors.Add("The saved-search reference is not valid.");
                }

                break;

            case WidgetSourceKind.SystemSeries:
                if (s.Metric is null)
                {
                    errors.Add("A collector-metric widget must name a metric.");
                }

                if (s.SavedSearchId is not null || !string.IsNullOrEmpty(s.InlineQuery))
                {
                    errors.Add("A collector-metric widget cannot also carry an event query.");
                }

                if (!DashboardWidgetCatalog.SystemSeriesVisualizations.Contains(widget.Visualization))
                {
                    errors.Add($"A collector metric cannot be shown as {Humanize(widget.Visualization)}.");
                }

                if (DashboardWidgetCatalog.IsListWidget(widget.Visualization))
                {
                    errors.Add("A collector metric cannot back a list widget.");
                }

                break;

            default:
                errors.Add("The widget source kind is not recognised.");
                break;
        }
    }

    private static void ValidateAggregation(WidgetDefinition widget, List<string> errors)
    {
        AggregationSpec a = widget.Aggregation;
        DashboardWidgetCatalog.VisualizationRule rule = DashboardWidgetCatalog.RuleFor(widget.Visualization);

        bool systemSeries = widget.Source.Kind == WidgetSourceKind.SystemSeries;

        // Group-by.
        string? groupBy = widget.Visualization == VisualizationType.SeverityDonut ? "severity" : a.GroupByField;
        if (groupBy is not null)
        {
            if (systemSeries)
            {
                errors.Add("A collector metric cannot be grouped.");
            }
            else if (!DashboardWidgetCatalog.IsGroupable(groupBy))
            {
                errors.Add($"'{groupBy}' cannot be used as a group-by field.");
            }
        }

        if (rule.RequiresGroupBy && groupBy is null)
        {
            errors.Add($"{Humanize(widget.Visualization)} needs a group-by field.");
        }

        if (rule.ForbidsGroupBy && a.GroupByField is not null)
        {
            errors.Add($"{Humanize(widget.Visualization)} cannot use a group-by field.");
        }

        // Bucket.
        if (rule.RequiresBucket && a.Bucket == BucketInterval.None)
        {
            errors.Add($"{Humanize(widget.Visualization)} needs a time bucket.");
        }

        if (rule.ForbidsBucket && a.Bucket != BucketInterval.None)
        {
            errors.Add($"{Humanize(widget.Visualization)} cannot use a time bucket.");
        }

        // Function + value field.
        if (systemSeries)
        {
            return; // the metric column is the value; function is fixed downstream
        }

        bool needsValue = a.Function is AggregationFunction.Sum or AggregationFunction.Average
            or AggregationFunction.Min or AggregationFunction.Max;

        if (needsValue)
        {
            if (string.IsNullOrWhiteSpace(a.ValueField))
            {
                errors.Add($"{a.Function} needs a numeric value field.");
            }
            else if (!DashboardWidgetCatalog.IsNumeric(a.ValueField))
            {
                errors.Add($"'{a.ValueField}' is not a numeric field.");
            }
        }

        if (a.Function == AggregationFunction.DistinctCount)
        {
            string? distinctField = a.ValueField ?? a.GroupByField;
            if (distinctField is null)
            {
                errors.Add("Distinct count needs a field to count distinct values of.");
            }
            else if (!DashboardWidgetCatalog.IsGroupable(distinctField) && !DashboardWidgetCatalog.IsNumeric(distinctField))
            {
                errors.Add($"'{distinctField}' cannot be counted distinctly.");
            }
        }

        if (a.Function == AggregationFunction.Count && !string.IsNullOrWhiteSpace(a.ValueField))
        {
            errors.Add("A count does not take a value field.");
        }

        // Top-N.
        if (groupBy is not null && (a.TopN < 1 || a.TopN > MaxTopN))
        {
            errors.Add($"Top-N must be between 1 and {MaxTopN}.");
        }
    }

    private static string Humanize(VisualizationType visualization) => visualization switch
    {
        VisualizationType.TimeSeriesLine => "A line chart",
        VisualizationType.TimeSeriesArea => "An area chart",
        VisualizationType.Bar => "A bar chart",
        VisualizationType.Counter => "A counter",
        VisualizationType.TopNTable => "A top-N table",
        VisualizationType.SeverityDonut => "A severity donut",
        VisualizationType.RateGauge => "A rate gauge",
        VisualizationType.RecentEventsTable => "A recent-events table",
        VisualizationType.DeviceStatusGrid => "A device status grid",
        _ => visualization.ToString(),
    };
}
