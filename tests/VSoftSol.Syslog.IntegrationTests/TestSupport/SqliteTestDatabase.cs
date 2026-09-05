using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// A throwaway migrated SQLite database in its own temp directory. Each test creates and
/// disposes its own instance and its own file (TESTING_STANDARDS.md §2.4).
/// </summary>
public sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private SqliteTestDatabase(SqliteDataOptions options)
    {
        Options = options;
        _directory = Path.GetDirectoryName(options.DatabasePath)!;
        Factory = new SqliteConnectionFactory(options);
        Repository = new SqliteLogRepository(Factory, Microsoft.Extensions.Options.Options.Create(options));
        Runner = new MigrationRunner(Factory, NullLogger<MigrationRunner>.Instance);
        Seeder = new DatabaseSeeder(Factory, NullLogger<DatabaseSeeder>.Instance);
    }

    public SqliteDataOptions Options { get; }

    public SqliteConnectionFactory Factory { get; }

    public SqliteLogRepository Repository { get; }

    public MigrationRunner Runner { get; }

    public DatabaseSeeder Seeder { get; }

    public string DatabasePath => Options.DatabasePath;

    public static SqliteTestDatabase CreateUnmigrated(Action<SqliteDataOptions>? configure = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = new SqliteDataOptions { DatabasePath = Path.Combine(dir, "syslog.db") };
        configure?.Invoke(options);
        return new SqliteTestDatabase(options);
    }

    public static async Task<SqliteTestDatabase> CreateAsync(Action<SqliteDataOptions>? configure = null)
    {
        SqliteTestDatabase db = CreateUnmigrated(configure);
        await db.Runner.MigrateAsync(CancellationToken.None);
        return db;
    }

    public static async Task<SqliteTestDatabase> CreateSeededAsync(Action<SqliteDataOptions>? configure = null)
    {
        SqliteTestDatabase db = await CreateAsync(configure);
        await db.Seeder.SeedAsync(CancellationToken.None);
        return db;
    }

    /// <summary>
    /// Runs the deferred search-index sync so a full-text query issued right after an
    /// append sees the new rows (in production, <c>SearchIndexMaintainer</c> does this).
    /// </summary>
    public Task<int> SyncSearchAsync() => Repository.SyncSearchIndexAsync(int.MaxValue, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(20); // let the OS release WAL/SHM handles
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort — temp dir
        }
    }
}
