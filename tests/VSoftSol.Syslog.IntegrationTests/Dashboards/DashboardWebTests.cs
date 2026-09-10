using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Dashboards;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 web surface: route authorization, role enforced <b>at the service</b>
/// (Read-Only / Auditor cannot create or edit; nobody edits a system dashboard), copy makes
/// an owned clone, and every mutation is audited.
/// </summary>
public sealed class DashboardWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public DashboardWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static ClaimsPrincipal Principal(Role role, string name = "admin") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role.ToString())], "Test"));

    private DashboardService ServiceAs(Role role, string name = "admin")
    {
        IServiceProvider sp = _factory.Services;
        return new DashboardService(
            sp.GetRequiredService<SqliteDashboardStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSavedSearchStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            new CurrentUserAccessor(new FakeAuthState(Principal(role, name))),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Users.SqliteUserStore>());
    }

    private static DashboardDefinition Simple(string name) => new()
    {
        Name = name,
        DefaultTimeRange = TimeSpan.FromHours(6),
        Widgets =
        [
            new WidgetDefinition
            {
                Id = "w1", Title = "Count", Source = WidgetSource.MatchAll,
                Aggregation = new AggregationSpec(), Visualization = VisualizationType.Counter,
            },
        ],
        Layout = [new WidgetLayout { WidgetId = "w1" }],
    };

    private async Task<long> CountAsync(string sql)
    {
        var factory = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Sqlite.SqliteConnectionFactory>();
        await using Microsoft.Data.Sqlite.SqliteConnection c = await factory.OpenAsync(CancellationToken.None);
        await using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("/dashboards")]
    [InlineData("/dashboards/1")]
    public async Task Routes_RequireAuthentication(string path)
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task List_AlwaysIncludesTheFourSystemDashboards()
    {
        IReadOnlyList<DashboardDefinition> list = await ServiceAs(Role.ReadOnly).ListAsync(CancellationToken.None);
        list.Count(d => d.IsSystem).Should().Be(4);
    }

    [Fact]
    public async Task Save_IsRefusedForReadOnly()
    {
        DashboardActionResult result = await ServiceAs(Role.ReadOnly).SaveAsync(Simple("ro attempt"), CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("permission");
    }

    [Fact]
    public async Task Save_ASystemDashboard_IsRefused()
    {
        DashboardDefinition system = (await ServiceAs(Role.Administrator).ListAsync(CancellationToken.None)).First(d => d.IsSystem);
        DashboardActionResult result = await ServiceAs(Role.Administrator).SaveAsync(system with { Name = "hijack" }, CancellationToken.None);
        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Create_ByOperator_IsAllowed_AndAudited()
    {
        DashboardActionResult result = await ServiceAs(Role.Operator).SaveAsync(Simple("operator board"), CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.DashboardId.Should().BeGreaterThan(0);
        (await CountAsync("SELECT COUNT(*) FROM audit_log WHERE action = 'dashboard.create' AND actor = 'admin';")).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Copy_ASystemDashboard_ProducesAnOwnedEditableClone()
    {
        DashboardDefinition net = (await ServiceAs(Role.Operator).ListAsync(CancellationToken.None)).First(d => d.SystemKey == "network-overview");

        DashboardActionResult result = await ServiceAs(Role.Operator).CopyAsync(net.Id, CancellationToken.None);
        result.Ok.Should().BeTrue();

        DashboardDefinition? copy = await ServiceAs(Role.Operator).GetAsync(result.DashboardId, CancellationToken.None);
        copy.Should().NotBeNull();
        copy!.IsSystem.Should().BeFalse();
        copy.OwnedByCurrentUser.Should().BeTrue();
        copy.Widgets.Should().HaveCount(net.Widgets.Count);
        copy.Name.Should().Contain("copy");
    }

    [Fact]
    public async Task Copy_IsAvailableToReadOnly()
    {
        DashboardDefinition sec = (await ServiceAs(Role.ReadOnly).ListAsync(CancellationToken.None)).First(d => d.SystemKey == "security-overview");
        DashboardActionResult result = await ServiceAs(Role.ReadOnly).CopyAsync(sec.Id, CancellationToken.None);
        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task SaveLayout_PersistsTheNewPositions()
    {
        long id = (await ServiceAs(Role.Operator).SaveAsync(Simple("layout board"), CancellationToken.None)).DashboardId;

        DashboardActionResult moved = await ServiceAs(Role.Operator).SaveLayoutAsync(
            id, [new WidgetLayout { WidgetId = "w1", Column = 4, Row = 2, ColumnSpan = 8, RowSpan = 2 }], CancellationToken.None);

        moved.Ok.Should().BeTrue();
        DashboardDefinition? reloaded = await ServiceAs(Role.Operator).GetAsync(id, CancellationToken.None);
        reloaded!.Layout.Single(l => l.WidgetId == "w1").Column.Should().Be(4);
        reloaded.Layout.Single(l => l.WidgetId == "w1").ColumnSpan.Should().Be(8);
    }

    [Fact]
    public async Task Save_AWidgetReferencingAMissingSavedSearch_IsRejected()
    {
        DashboardDefinition bad = Simple("bad ref") with
        {
            Widgets =
            [
                new WidgetDefinition
                {
                    Id = "w1", Title = "Ghost", Source = WidgetSource.Saved(987654),
                    Aggregation = new AggregationSpec(), Visualization = VisualizationType.Counter,
                },
            ],
        };

        DashboardActionResult result = await ServiceAs(Role.Operator).SaveAsync(bad, CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.Errors.Should().ContainMatch("*saved search*");
    }

    [Fact]
    public void WidgetPicker_BuildsAValidWidget_FromSelections()
    {
        (WidgetDefinition widget, ValidationResult validation) = WidgetPickerModel.Build(
            title: "Noisiest devices",
            source: WidgetSource.MatchAll,
            function: AggregationFunction.Count,
            valueField: null,
            groupByField: "hostname",
            bucket: BucketInterval.None,
            topN: 10,
            visualization: VisualizationType.Bar);

        validation.Ok.Should().BeTrue();
        widget.Aggregation.GroupByField.Should().Be("hostname");
        widget.Visualization.Should().Be(VisualizationType.Bar);
    }
}
