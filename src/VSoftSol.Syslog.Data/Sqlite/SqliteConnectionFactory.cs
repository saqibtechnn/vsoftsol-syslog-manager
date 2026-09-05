using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Data.Sqlite;

/// <summary>
/// Opens configured SQLite connections and owns the single-writer discipline.
///
/// <para>
/// WAL mode lets many readers run concurrently with exactly one writer. SQLite itself
/// permits only one writer at a time; rather than rely on <c>busy_timeout</c> alone under
/// our own load, every write transaction in this process is serialised through
/// <see cref="AcquireWriteLockAsync"/>. Readers never take the lock.
/// </para>
/// </summary>
public sealed class SqliteConnectionFactory : IDisposable
{
    private readonly SqliteDataOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;
    private int _initialised;

    public SqliteConnectionFactory(IOptions<SqliteDataOptions> options)
        : this(options.Value)
    {
    }

    public SqliteConnectionFactory(SqliteDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        _options = options;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath => _options.DatabasePath;

    /// <summary>Opens a connection with the standard per-connection pragmas applied.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Acquires the process-wide write lock. Dispose the result to release it. Hold it
    /// only for the duration of a single write transaction.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_writeLock);
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // journal_mode is a database-level setting; setting it once (idempotently) is enough,
        // but it is cheap and safe to assert on every open.
        bool first = Interlocked.Exchange(ref _initialised, 1) == 0;

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA busy_timeout = {(int)_options.BusyTimeout.TotalMilliseconds};
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -{_options.PageCacheKib};
            PRAGMA wal_autocheckpoint = {_options.WalAutoCheckpointPages};
            {(first ? "PRAGMA journal_mode = WAL;" : string.Empty)}
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _writeLock.Dispose();

    private sealed class Releaser : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
