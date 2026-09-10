using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

public sealed class DashboardPersistenceTests
{
    private static DashboardDefinition NewDashboard(string name) => new()
    {
        Name = name,
        DefaultTimeRange = TimeSpan.FromHours(12),
        Widgets =
        [
            new WidgetDefinition
            {
                Id = "w1",
                Title = "Count",
                Source = WidgetSource.MatchAll,
                Aggregation = new AggregationSpec(),
                Visualization = VisualizationType.Counter,
            },
        ],
        Layout = [new WidgetLayout { WidgetId = "w1", Column = 0, Row = 0, ColumnSpan = 4, RowSpan = 1 }],
    };

    private static async Task<long> AdminId(SqliteTestDatabase db)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM users WHERE username = 'admin';";
        return (long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task Create_ThenGet_RoundTripsTheWidgetsAndLayout()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long uid = await AdminId(db);

        long id = await store.CreateAsync(NewDashboard("My board"), uid, "admin", CancellationToken.None);
        DashboardDefinition? loaded = await store.GetAsync(id, uid, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("My board");
        loaded.Widgets.Should().ContainSingle(w => w.Id == "w1" && w.Visualization == VisualizationType.Counter);
        loaded.Layout.Should().ContainSingle(l => l.WidgetId == "w1" && l.ColumnSpan == 4);
        loaded.OwnedByCurrentUser.Should().BeTrue();
        loaded.IsSystem.Should().BeFalse();
    }

    [Fact]
    public async Task Version_BumpsOnEveryMutation_NotOnRead()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long uid = await AdminId(db);

        long before = store.Version;
        long id = await store.CreateAsync(NewDashboard("v"), uid, "admin", CancellationToken.None);
        store.Version.Should().BeGreaterThan(before);

        long afterCreate = store.Version;
        await store.GetAsync(id, uid, CancellationToken.None);
        store.Version.Should().Be(afterCreate);

        await store.UpdateAsync((await store.GetAsync(id, uid, CancellationToken.None))! with { Name = "v2" }, uid, "admin", CancellationToken.None);
        store.Version.Should().BeGreaterThan(afterCreate);
    }

    [Fact]
    public async Task Update_ByANonOwner_DoesNothing()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long owner = await AdminId(db);
        long id = await store.CreateAsync(NewDashboard("owned") with { IsShared = true }, owner, "admin", CancellationToken.None);

        bool updated = await store.UpdateAsync(
            (await store.GetAsync(id, owner, CancellationToken.None))! with { Name = "hijacked" },
            requestingUserId: 999_999, "mallory", CancellationToken.None);

        updated.Should().BeFalse();
        (await store.GetAsync(id, owner, CancellationToken.None))!.Name.Should().Be("owned");
    }

    [Fact]
    public async Task Delete_ASystemDashboard_IsRefused()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long uid = await AdminId(db);

        DashboardDefinition system = (await store.GetBySystemKeyAsync("network-overview", CancellationToken.None))!;
        bool deleted = await store.DeleteAsync(system.Id, uid, CancellationToken.None);

        deleted.Should().BeFalse();
        (await store.GetBySystemKeyAsync("network-overview", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Update_ASystemDashboard_IsRefused()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long uid = await AdminId(db);
        DashboardDefinition system = (await store.GetBySystemKeyAsync("collector-health", CancellationToken.None))!;

        bool updated = await store.UpdateAsync(system with { Name = "nope" }, uid, "admin", CancellationToken.None);

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task List_ShowsSystemDashboardsPlusOwnPlusShared_NotOthersPrivate()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long alice = await AdminId(db);
        const long bob = 424242;

        await store.CreateAsync(NewDashboard("alice-private"), alice, "alice", CancellationToken.None);
        await store.CreateAsync(NewDashboard("alice-shared") with { IsShared = true }, alice, "alice", CancellationToken.None);

        IReadOnlyList<DashboardDefinition> bobSees = await store.ListForUserAsync(bob, CancellationToken.None);

        bobSees.Should().Contain(d => d.SystemKey == "network-overview");
        bobSees.Should().Contain(d => d.Name == "alice-shared");
        bobSees.Should().NotContain(d => d.Name == "alice-private");
        bobSees.Take(4).Should().OnlyContain(d => d.IsSystem, "system dashboards sort first");
    }

    [Fact]
    public async Task Create_TwoDashboardsWithTheSameNameForOneUser_IsRejected()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long uid = await AdminId(db);
        await store.CreateAsync(NewDashboard("dup"), uid, "admin", CancellationToken.None);

        Func<Task> second = () => store.CreateAsync(NewDashboard("dup"), uid, "admin", CancellationToken.None);
        await second.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Get_AnotherUsersPrivateDashboardById_ReturnsNull()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDashboardStore(db.Factory);
        long alice = await AdminId(db);
        long id = await store.CreateAsync(NewDashboard("secret"), alice, "alice", CancellationToken.None);

        (await store.GetAsync(id, requestingUserId: 777, CancellationToken.None)).Should().BeNull();
    }
}
