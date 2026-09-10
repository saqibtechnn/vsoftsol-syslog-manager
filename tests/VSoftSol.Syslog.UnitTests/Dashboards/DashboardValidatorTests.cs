using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

public sealed class DashboardValidatorTests
{
    private static WidgetDefinition Ok(string id) => new()
    {
        Id = id,
        Title = id,
        Source = WidgetSource.MatchAll,
        Aggregation = new AggregationSpec(),
        Visualization = VisualizationType.Counter,
    };

    private static DashboardDefinition Dashboard(params WidgetDefinition[] widgets) => new()
    {
        Name = "Ops",
        DefaultTimeRange = TimeSpan.FromHours(24),
        Widgets = widgets,
        Layout = [.. widgets.Select(w => new WidgetLayout { WidgetId = w.Id })],
    };

    [Fact]
    public void Validate_AnEmptyName_IsRejected()
    {
        DashboardValidator.Validate(Dashboard() with { Name = "  " }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_TooManyWidgets_IsRejected()
    {
        WidgetDefinition[] widgets = [.. Enumerable.Range(0, DashboardValidator.MaxWidgets + 1).Select(i => Ok($"w{i}"))];
        ValidationResult r = DashboardValidator.Validate(Dashboard(widgets));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*at most 24 widgets*");
    }

    [Fact]
    public void Validate_DuplicateWidgetIds_IsRejected()
    {
        ValidationResult r = DashboardValidator.Validate(Dashboard(Ok("dup"), Ok("dup")));

        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*share the id*");
    }

    [Fact]
    public void Validate_LayoutReferencingAMissingWidget_IsRejected()
    {
        DashboardDefinition d = Dashboard(Ok("w1")) with
        {
            Layout = [new WidgetLayout { WidgetId = "ghost" }],
        };

        ValidationResult r = DashboardValidator.Validate(d);
        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("*does not exist*");
    }

    [Fact]
    public void Validate_PropagatesAWidgetError_WithTheWidgetName()
    {
        WidgetDefinition bad = Ok("w1") with
        {
            Title = "Broken",
            Visualization = VisualizationType.Bar,
            Aggregation = new AggregationSpec { GroupByField = null },
        };

        ValidationResult r = DashboardValidator.Validate(Dashboard(bad));
        r.Ok.Should().BeFalse();
        r.Errors.Should().ContainMatch("Widget 'Broken': *group-by*");
    }

    [Fact]
    public void Validate_AGoodDashboard_IsOk()
    {
        DashboardValidator.Validate(Dashboard(Ok("a"), Ok("b"), Ok("c"))).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_AnAbsurdTimeRange_IsRejected()
    {
        DashboardValidator.Validate(Dashboard(Ok("a")) with { DefaultTimeRange = TimeSpan.FromDays(1000) })
            .Ok.Should().BeFalse();
    }
}
