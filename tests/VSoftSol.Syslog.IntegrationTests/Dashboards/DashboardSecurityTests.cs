using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Dashboards;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 security validation. The headline risk: two users with different scopes load
/// the same shared dashboard widget and each must see only their own data — an aggregate
/// that includes out-of-scope events is a data leak even with no row shown. Also stored
/// XSS through widget titles / dashboard names / log-derived category labels, IDOR on
/// dashboard ids, and cache keying by scope.
/// </summary>
public sealed class DashboardSecurityTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public DashboardSecurityTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static ClaimsPrincipal Principal(string name, Role role, long[]? streams = null) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role.ToString()),
                .. streams is null ? Array.Empty<Claim>() : [new Claim("vsoftsol:streams", string.Join(',', streams))],
            ], "Test"));

    private WidgetDataService WidgetsFor(ClaimsPrincipal principal)
    {
        IServiceProvider sp = _factory.Services;
        return new WidgetDataService(
            sp.GetRequiredService<SqliteAggregationReader>(),
            sp.GetRequiredService<SystemSeriesReader>(),
            sp.GetRequiredService<AggregationCache>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Scoping.ScopedEventReader>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSavedSearchStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Devices.SqliteDeviceStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSearchFacets>(),
            new CurrentUserAccessor(new FakeAuthState(principal)),
            sp.GetRequiredService<TimeProvider>(),
            Options.Create(new DashboardOptions()));
    }

    private DashboardService DashboardsFor(ClaimsPrincipal principal)
    {
        IServiceProvider sp = _factory.Services;
        return new DashboardService(
            sp.GetRequiredService<SqliteDashboardStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSavedSearchStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            new CurrentUserAccessor(new FakeAuthState(principal)),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Users.SqliteUserStore>());
    }

    private async Task<long> ExecAsync(string sql)
    {
        var factory = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Sqlite.SqliteConnectionFactory>();
        await using SqliteConnection c = await factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        object? r = await cmd.ExecuteScalarAsync(CancellationToken.None);
        return r is null or DBNull ? 0 : Convert.ToInt64(r, CultureInfo.InvariantCulture);
    }

    private static WidgetDefinition CountByHost(string tag) => new()
    {
        Id = "w-" + tag,
        Title = "By host",
        Source = WidgetSource.Query($"app:{tag}"),
        Aggregation = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 50 },
        Visualization = VisualizationType.Bar,
    };

    [Fact]
    public async Task CrossScope_TheSameSharedWidget_ShowsEachViewerOnlyTheirOwnData()
    {
        // Seed two streams with disjoint events, tagged so this test is isolated.
        long sA = await ExecAsync("INSERT INTO streams (name, enabled, sort_order, created_utc) VALUES ('sec-A', 1, 90, 't'); SELECT last_insert_rowid();");
        long sB = await ExecAsync("INSERT INTO streams (name, enabled, sort_order, created_utc) VALUES ('sec-B', 1, 91, 't'); SELECT last_insert_rowid();");

        var repo = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        var now = DateTimeOffset.UtcNow;
        var eventsA = Enumerable.Range(0, 30).Select(i => Event("host-A", "scopetag", now.AddMinutes(-i))).ToList();
        var eventsB = Enumerable.Range(0, 7).Select(i => Event("host-B", "scopetag", now.AddMinutes(-i))).ToList();
        IReadOnlyList<long> idsA = await repo.AppendBatchAsync(eventsA, CancellationToken.None);
        IReadOnlyList<long> idsB = await repo.AppendBatchAsync(eventsB, CancellationToken.None);
        foreach (long id in idsA) { await ExecAsync($"INSERT INTO event_streams (event_id, stream_id) VALUES ({id}, {sA});"); }
        foreach (long id in idsB) { await ExecAsync($"INSERT INTO event_streams (event_id, stream_id) VALUES ({id}, {sB});"); }

        WidgetDefinition widget = CountByHost("scopetag");

        WidgetData a = await WidgetsFor(Principal("ua", Role.Operator, [sA])).LoadAsync(widget, TimeSpan.FromDays(1), CancellationToken.None);
        WidgetData b = await WidgetsFor(Principal("ub", Role.Operator, [sB])).LoadAsync(widget, TimeSpan.FromDays(1), CancellationToken.None);

        a.Aggregation!.GroupKeys().Should().Contain("host-A").And.NotContain("host-B");
        a.Aggregation.Points.Sum(p => p.Value).Should().Be(30);

        b.Aggregation!.GroupKeys().Should().Contain("host-B").And.NotContain("host-A");
        b.Aggregation.Points.Sum(p => p.Value).Should().Be(7);
    }

    [Fact]
    public async Task Cache_IsKeyedByScope_SoOneViewersResultIsNeverServedToAnother()
    {
        long sA = await ExecAsync("INSERT INTO streams (name, enabled, sort_order, created_utc) VALUES ('cache-A', 1, 92, 't'); SELECT last_insert_rowid();");
        long sB = await ExecAsync("INSERT INTO streams (name, enabled, sort_order, created_utc) VALUES ('cache-B', 1, 93, 't'); SELECT last_insert_rowid();");
        var repo = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<long> idsA = await repo.AppendBatchAsync([.. Enumerable.Range(0, 12).Select(i => Event("ca", "cachetag", now.AddMinutes(-i)))], CancellationToken.None);
        IReadOnlyList<long> idsB = await repo.AppendBatchAsync([.. Enumerable.Range(0, 3).Select(i => Event("cb", "cachetag", now.AddMinutes(-i)))], CancellationToken.None);
        foreach (long id in idsA) { await ExecAsync($"INSERT INTO event_streams (event_id, stream_id) VALUES ({id}, {sA});"); }
        foreach (long id in idsB) { await ExecAsync($"INSERT INTO event_streams (event_id, stream_id) VALUES ({id}, {sB});"); }

        var widget = new WidgetDefinition
        {
            Id = "cache-w",
            Title = "n",
            Source = WidgetSource.Query("app:cachetag"),
            Aggregation = new AggregationSpec { Function = AggregationFunction.Count },
            Visualization = VisualizationType.Counter,
        };

        // Viewer A primes the cache, then viewer B asks for the identical widget.
        WidgetData a = await WidgetsFor(Principal("ca-u", Role.Operator, [sA])).LoadAsync(widget, TimeSpan.FromDays(1), CancellationToken.None);
        WidgetData b = await WidgetsFor(Principal("cb-u", Role.Operator, [sB])).LoadAsync(widget, TimeSpan.FromDays(1), CancellationToken.None);

        a.Aggregation!.Scalar().Should().Be(12);
        b.Aggregation!.Scalar().Should().Be(3);
    }

    [Fact]
    public async Task StoredXss_InWidgetTitleAndDashboardName_IsPersistedVerbatim_NotSanitized()
    {
        const string payload = "<script>alert(1)</script>";
        var dashboard = new DashboardDefinition
        {
            Name = "XSS " + payload,
            DefaultTimeRange = TimeSpan.FromHours(6),
            Widgets =
            [
                new WidgetDefinition
                {
                    Id = "x1", Title = payload, Source = WidgetSource.MatchAll,
                    Aggregation = new AggregationSpec(), Visualization = VisualizationType.Counter,
                },
            ],
            Layout = [new WidgetLayout { WidgetId = "x1" }],
        };

        long id = (await DashboardsFor(Principal("admin", Role.Administrator)).SaveAsync(dashboard, CancellationToken.None)).DashboardId;
        DashboardDefinition? reloaded = await DashboardsFor(Principal("admin", Role.Administrator)).GetAsync(id, CancellationToken.None);

        reloaded!.Name.Should().Be("XSS " + payload, "the raw value is preserved; encoding happens at render (Blazor default)");
        reloaded.Widgets.Single().Title.Should().Be(payload);
    }

    [Fact]
    public async Task Idor_AnotherUsersPrivateDashboard_IsNotReturnedById()
    {
        long id = (await DashboardsFor(Principal("admin", Role.Administrator))
            .SaveAsync(new DashboardDefinition
            {
                Name = "admin private " + Guid.NewGuid().ToString("N")[..6],
                DefaultTimeRange = TimeSpan.FromHours(6),
                Widgets = [new WidgetDefinition { Id = "w1", Title = "t", Source = WidgetSource.MatchAll, Aggregation = new AggregationSpec(), Visualization = VisualizationType.Counter }],
                Layout = [new WidgetLayout { WidgetId = "w1" }],
            }, CancellationToken.None)).DashboardId;

        // A different principal (unknown username → uid resolves to -1) must not see it.
        DashboardDefinition? asOther = await DashboardsFor(Principal("mallory", Role.Operator)).GetAsync(id, CancellationToken.None);
        asOther.Should().BeNull();
    }

    private static SyslogEvent Event(string host, string app, DateTimeOffset when) => new()
    {
        ReceivedUtc = when.ToUniversalTime(),
        SourceIp = "10.7.7.7",
        Hostname = host,
        AppName = app,
        Facility = Facility.Local0,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = "m",
        RawMessage = System.Text.Encoding.UTF8.GetBytes("m"),
        ParseStatus = ParseStatus.Rfc5424,
    };
}
