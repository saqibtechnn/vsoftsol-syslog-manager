using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Bundles a throwaway migrated database with the Phase 9 readers and stores, plus a
/// controllable clock. Each test owns its instance.
/// </summary>
public sealed class DashboardTestHarness : IAsyncDisposable
{
    private DashboardTestHarness(SqliteTestDatabase db, FakeTimeProvider clock)
    {
        Db = db;
        Clock = clock;
        Aggregation = new SqliteAggregationReader(db.Factory);
        SystemSeries = new SystemSeriesReader(db.Factory);
        Dashboards = new SqliteDashboardStore(db.Factory, clock);
        SavedSearches = new SqliteSavedSearchStore(db.Factory, clock);
        Scoped = new ScopedEventReader(db.Repository, db.Factory, Options.Create(new SearchOptions()));
        Cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(15));
    }

    public SqliteTestDatabase Db { get; }

    public FakeTimeProvider Clock { get; }

    public SqliteAggregationReader Aggregation { get; }

    public SystemSeriesReader SystemSeries { get; }

    public SqliteDashboardStore Dashboards { get; }

    public SqliteSavedSearchStore SavedSearches { get; }

    public ScopedEventReader Scoped { get; }

    public AggregationCache Cache { get; }

    public static async Task<DashboardTestHarness> CreateAsync(DateTimeOffset? clockStart = null, bool seed = true)
    {
        SqliteTestDatabase db = seed
            ? await SqliteTestDatabase.CreateSeededAsync()
            : await SqliteTestDatabase.CreateAsync();
        var clock = new FakeTimeProvider(clockStart ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        return new DashboardTestHarness(db, clock);
    }

    public async Task SeedEventsAsync(IEnumerable<VSoftSol.Syslog.Core.Events.SyslogEvent> events)
    {
        await Db.Repository.AppendBatchAsync([.. events], CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
