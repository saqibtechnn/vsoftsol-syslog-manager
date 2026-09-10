using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

public sealed class DashboardWidgetCatalogTests
{
    [Theory]
    [InlineData("hostname", true)]
    [InlineData("severity", true)]
    [InlineData("parse_status", true)]
    [InlineData("field.srcport", true)]
    [InlineData("message", false)]
    [InlineData("occurrence_count", false)]
    [InlineData("field.bad name", false)]
    [InlineData(null, false)]
    public void IsGroupable_MatchesTheAllowList(string? field, bool expected)
    {
        DashboardWidgetCatalog.IsGroupable(field).Should().Be(expected);
    }

    [Theory]
    [InlineData("severity", true)]
    [InlineData("facility", true)]
    [InlineData("occurrence_count", true)]
    [InlineData("field.bytes", true)]
    [InlineData("hostname", false)]
    [InlineData("message", false)]
    public void IsNumeric_MatchesTheAllowList(string field, bool expected)
    {
        DashboardWidgetCatalog.IsNumeric(field).Should().Be(expected);
    }

    [Fact]
    public void RuleFor_TimeSeries_RequiresABucket()
    {
        DashboardWidgetCatalog.RuleFor(VisualizationType.TimeSeriesLine).RequiresBucket.Should().BeTrue();
    }

    [Fact]
    public void RuleFor_Bar_RequiresGroupBy_AndForbidsBucket()
    {
        DashboardWidgetCatalog.VisualizationRule rule = DashboardWidgetCatalog.RuleFor(VisualizationType.Bar);
        rule.RequiresGroupBy.Should().BeTrue();
        rule.ForbidsBucket.Should().BeTrue();
    }

    [Fact]
    public void IsListWidget_IsTrueForRecentEventsAndDeviceGrid_Only()
    {
        DashboardWidgetCatalog.IsListWidget(VisualizationType.RecentEventsTable).Should().BeTrue();
        DashboardWidgetCatalog.IsListWidget(VisualizationType.DeviceStatusGrid).Should().BeTrue();
        DashboardWidgetCatalog.IsListWidget(VisualizationType.Counter).Should().BeFalse();
    }

    [Fact]
    public void SystemSeriesVisualizations_AreTimeSeriesCounterAndGaugeOnly()
    {
        DashboardWidgetCatalog.SystemSeriesVisualizations.Should().BeEquivalentTo(
        [
            VisualizationType.TimeSeriesLine,
            VisualizationType.TimeSeriesArea,
            VisualizationType.Counter,
            VisualizationType.RateGauge,
        ]);
    }

    [Fact]
    public void GroupableFields_DoesNotIncludeMessage()
    {
        DashboardWidgetCatalog.GroupableFields.Should().NotContain(f => f.Name == "message");
    }
}
