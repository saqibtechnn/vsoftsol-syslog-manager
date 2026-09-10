using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 build item 6 — the four shipped dashboards must be genuinely useful on day one:
/// every widget validates, and every event-backed widget compiles and runs against a
/// realistic dataset without error.
/// </summary>
public sealed class DefaultDashboardsTests : IAsyncLifetime
{
    private DashboardTestHarness _h = null!;

    public async Task InitializeAsync()
    {
        _h = await DashboardTestHarness.CreateAsync(seed: false);
        await _h.SeedEventsAsync(AggregationFixture.Build());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public void There_AreFourDefaultDashboards_AllSystemAndShared()
    {
        DefaultDashboards.All.Should().HaveCount(4);
        DefaultDashboards.All.Should().OnlyContain(d => d.IsSystem && d.IsShared && d.SystemKey != null);
    }

    [Fact]
    public void EveryDefaultWidget_PassesValidation()
    {
        foreach (DashboardDefinition dashboard in DefaultDashboards.All)
        {
            ValidationResult result = DashboardValidator.Validate(dashboard);
            result.Ok.Should().BeTrue($"{dashboard.Name}: {string.Join(" | ", result.Errors)}");
        }
    }

    [Fact]
    public async Task EveryEventBackedWidget_CompilesAndRuns()
    {
        var from = AggregationFixture.WindowStart;
        var to = AggregationFixture.WindowEnd;

        foreach (DashboardDefinition dashboard in DefaultDashboards.All)
        {
            foreach (WidgetDefinition widget in dashboard.Widgets)
            {
                if (widget.Source.Kind != WidgetSourceKind.EventQuery
                    || DashboardWidgetCatalog.IsListWidget(widget.Visualization))
                {
                    continue;
                }

                string query = widget.Source.InlineQuery ?? string.Empty;
                string groupBy = widget.Visualization == VisualizationType.SeverityDonut ? "severity" : widget.Aggregation.GroupByField ?? "-";
                AggregationSpec spec = widget.Visualization == VisualizationType.SeverityDonut
                    ? widget.Aggregation with { GroupByField = "severity" }
                    : widget.Aggregation;

                SqliteAggregationReader.AggregationOutcome outcome = await _h.Aggregation.AggregateAsync(
                    UserScope.Unrestricted, query, spec, from, to,
                    spec.Bucket, CancellationToken.None);

                outcome.Status.Should().Be(
                    SqliteAggregationReader.AggregationStatus.Ok,
                    $"{dashboard.Name} / {widget.Title} (group {groupBy}): {outcome.Detail}");
            }
        }
    }

    [Fact]
    public async Task NetworkOverview_MessageRateWidget_ProducesANonEmptySeries()
    {
        DashboardDefinition net = DefaultDashboards.All.Single(d => d.SystemKey == "network-overview");
        WidgetDefinition rate = net.Widgets.Single(w => w.Id == "net-msgrate");

        SqliteAggregationReader.AggregationOutcome outcome = await _h.Aggregation.AggregateAsync(
            UserScope.Unrestricted, rate.Source.InlineQuery ?? "", rate.Aggregation,
            AggregationFixture.WindowStart, AggregationFixture.WindowEnd, rate.Aggregation.Bucket, CancellationToken.None);

        outcome.Result.Points.Sum(p => p.Value).Should().Be(AggregationFixture.EventCount);
        outcome.Result.Plan.Should().NotBeNull();
    }
}
