using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

/// <summary>
/// PHASE_09 — the widget picker cannot produce an invalid widget, and an imported
/// definition is rejected before it is stored. These lock the coherence rules.
/// </summary>
public sealed class WidgetValidatorTests
{
    private static WidgetDefinition Widget(
        WidgetSource? source = null,
        AggregationSpec? aggregation = null,
        VisualizationType visualization = VisualizationType.Counter) =>
        new()
        {
            Id = "w1",
            Title = "Test",
            Source = source ?? WidgetSource.MatchAll,
            Aggregation = aggregation ?? new AggregationSpec(),
            Visualization = visualization,
        };

    [Fact]
    public void Validate_ACountCounter_IsOk()
    {
        WidgetValidator.Validate(Widget()).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_ALineChartWithoutABucket_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { Bucket = BucketInterval.None },
            visualization: VisualizationType.TimeSeriesLine));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*needs a time bucket*");
    }

    [Fact]
    public void Validate_ABarChartWithoutAGroupBy_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { GroupByField = null },
            visualization: VisualizationType.Bar));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*needs a group-by field*");
    }

    [Fact]
    public void Validate_ACounterWithAGroupBy_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { GroupByField = "hostname" },
            visualization: VisualizationType.Counter));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*cannot use a group-by field*");
    }

    [Fact]
    public void Validate_GroupByMessage_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { GroupByField = "message" },
            visualization: VisualizationType.Bar));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*'message' cannot be used as a group-by field*");
    }

    [Fact]
    public void Validate_SumWithoutAValueField_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { Function = AggregationFunction.Sum, Bucket = BucketInterval.OneHour },
            visualization: VisualizationType.TimeSeriesLine));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*needs a numeric value field*");
    }

    [Fact]
    public void Validate_SumOfANonNumericField_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec
            {
                Function = AggregationFunction.Average,
                ValueField = "hostname",
                Bucket = BucketInterval.OneHour,
            },
            visualization: VisualizationType.TimeSeriesLine));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*not a numeric field*");
    }

    [Fact]
    public void Validate_SumOfAnExtractedField_IsOk()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec
            {
                Function = AggregationFunction.Sum,
                ValueField = "field.bytes",
                Bucket = BucketInterval.OneHour,
            },
            visualization: VisualizationType.TimeSeriesArea));

        r.Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_CountWithAValueField_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { Function = AggregationFunction.Count, ValueField = "severity" }));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*count does not take a value field*");
    }

    [Fact]
    public void Validate_TopNOutOfRange_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { GroupByField = "hostname", TopN = 999 },
            visualization: VisualizationType.TopNTable));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*Top-N must be between 1 and 50*");
    }

    [Fact]
    public void Validate_ASystemMetricShownAsABar_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            source: WidgetSource.System(SystemMetric.IngestRate),
            aggregation: new AggregationSpec { GroupByField = "hostname" },
            visualization: VisualizationType.Bar));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*cannot be shown as*");
    }

    [Fact]
    public void Validate_ASystemMetricCounter_IsOk()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            source: WidgetSource.System(SystemMetric.DiskFreeBytes),
            aggregation: new AggregationSpec(),
            visualization: VisualizationType.Counter));

        r.Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_ASystemMetricWithAnInlineQuery_IsRejected()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            source: new WidgetSource { Kind = WidgetSourceKind.SystemSeries, Metric = SystemMetric.IngestRate, InlineQuery = "failed" },
            visualization: VisualizationType.Counter));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*cannot also carry an event query*");
    }

    [Fact]
    public void Validate_ARecentEventsTable_IgnoresTheAggregation()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec { Function = AggregationFunction.Sum, GroupByField = "message" },
            visualization: VisualizationType.RecentEventsTable));

        r.Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_ADeviceStatusGrid_IsOk()
    {
        WidgetValidator.Validate(Widget(visualization: VisualizationType.DeviceStatusGrid)).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_ASeverityDonut_NeedsNoExplicitGroupBy()
    {
        ValidationResult r = WidgetValidator.Validate(Widget(
            aggregation: new AggregationSpec(),
            visualization: VisualizationType.SeverityDonut));

        r.Ok.Should().BeTrue();
    }
}
