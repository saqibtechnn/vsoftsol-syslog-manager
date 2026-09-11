using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Retention.Compression;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>Bundles a throwaway migrated database with the Phase 10 stores, engine, and a
/// controllable clock. Each test owns its instance.</summary>
public sealed class RetentionTestHarness : IAsyncDisposable
{
    private RetentionTestHarness(SqliteTestDatabase db, FakeTimeProvider clock)
    {
        Db = db;
        Clock = clock;
        Compressor = new CompressorFactory();
        Policies = new SqliteRetentionPolicyStore(db.Factory, clock);
        Archives = new SqliteArchiveStore(db.Factory, clock);
        Restores = new SqliteRestoreStore(db.Factory, clock);
        Engine = new SqliteRetentionEngine(db.Factory, Policies, Archives, Restores, Compressor, clock);
        Verifier = new SqliteArchiveVerifier(Archives, Policies);
        Estimates = new RetentionEstimateReader(db.Factory, clock);
        Scoped = new ScopedEventReader(db.Repository, db.Factory, Options.Create(new SearchOptions()));
        Aggregation = new SqliteAggregationReader(db.Factory);
        SavedSearches = new SqliteSavedSearchStore(db.Factory, clock);
        AuditLog = new VSoftSol.Syslog.Data.Audit.SqliteAuditLog(db.Factory, clock);
        Reports = new SqliteReportStore(db.Factory, clock);
        SmtpSettings = new SqliteReportSmtpSettingsStore(db.Factory, clock);
        Content = new ReportContentReader(Scoped, Aggregation, SavedSearches, AuditLog, Archives);
    }

    public SqliteTestDatabase Db { get; }

    public FakeTimeProvider Clock { get; }

    public CompressorFactory Compressor { get; }

    public SqliteRetentionPolicyStore Policies { get; }

    public SqliteArchiveStore Archives { get; }

    public SqliteRestoreStore Restores { get; }

    public SqliteRetentionEngine Engine { get; }

    public SqliteArchiveVerifier Verifier { get; }

    public RetentionEstimateReader Estimates { get; }

    public ScopedEventReader Scoped { get; }

    public SqliteAggregationReader Aggregation { get; }

    public SqliteSavedSearchStore SavedSearches { get; }

    public VSoftSol.Syslog.Data.Audit.SqliteAuditLog AuditLog { get; }

    public SqliteReportStore Reports { get; }

    public SqliteReportSmtpSettingsStore SmtpSettings { get; }

    public ReportContentReader Content { get; }

    public static async Task<RetentionTestHarness> CreateAsync(DateTimeOffset? clockStart = null, bool seed = true)
    {
        SqliteTestDatabase db = seed
            ? await SqliteTestDatabase.CreateSeededAsync()
            : await SqliteTestDatabase.CreateAsync();
        var clock = new FakeTimeProvider(clockStart ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        return new RetentionTestHarness(db, clock);
    }

    public async Task<IReadOnlyList<long>> SeedEventsAsync(IEnumerable<VSoftSol.Syslog.Core.Events.SyslogEvent> events)
    {
        IReadOnlyList<long> ids = await Db.Repository.AppendBatchAsync([.. events], CancellationToken.None);
        await Db.SyncSearchAsync();
        return ids;
    }

    /// <summary>Creates a bare test user (no password, disabled login — FK-target only) and
    /// returns its id.</summary>
    public async Task<long> CreateUserAsync(string username)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = await Db.Factory.OpenAsync(CancellationToken.None);
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (username, display_name, role_id, must_change_password, is_enabled, created_utc)
            VALUES ($name, $name, 2, 0, 1, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", username);
        command.Parameters.AddWithValue("$now", Clock.GetUtcNow().ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    /// <summary>Creates a bare test stream (no match rule — routing is set explicitly on the
    /// fixture events via <c>StreamIds</c>) and returns its id.</summary>
    public async Task<long> CreateStreamAsync(string name, bool isCatchAll = false)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = await Db.Factory.OpenAsync(CancellationToken.None);
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO streams (name, is_catch_all, enabled, created_utc) VALUES ($name, $catchAll, 1, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$catchAll", isCatchAll ? 1 : 0);
        command.Parameters.AddWithValue("$now", Clock.GetUtcNow().ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
