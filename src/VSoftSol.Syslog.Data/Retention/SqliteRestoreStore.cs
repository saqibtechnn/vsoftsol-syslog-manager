using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>CRUD for <see cref="RestoreRecord"/> (PHASE_10 build item 5).</summary>
public sealed class SqliteRestoreStore
{
    private const string Columns =
        "restore_id, archive_id, requested_by, requested_utc, expires_utc, event_count, status, expired_utc";

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteRestoreStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<long> CreateAsync(long archiveId, string requestedBy, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO archive_restores (archive_id, requested_by, requested_utc, expires_utc, event_count, status)
            VALUES ($aid, $by, $now, $expires, 0, 'active');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$aid", archiveId);
        command.Parameters.AddWithValue("$by", requestedBy);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expires", StorageFormat.Timestamp(expiresUtc));

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task SetEventCountAsync(long restoreId, int eventCount, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE archive_restores SET event_count = $count WHERE restore_id = $id;";
        command.Parameters.AddWithValue("$count", eventCount);
        command.Parameters.AddWithValue("$id", restoreId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RestoreRecord?> GetAsync(long restoreId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM archive_restores WHERE restore_id = $id;";
        command.Parameters.AddWithValue("$id", restoreId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<RestoreRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM archive_restores WHERE status = 'active' ORDER BY requested_utc DESC;";

        var result = new List<RestoreRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    /// <summary>Active restores whose <c>expires_utc</c> has passed.</summary>
    public async Task<IReadOnlyList<RestoreRecord>> ListExpiredAsync(int limit, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM archive_restores
            WHERE status = 'active' AND expires_utc <= $now
            ORDER BY expires_utc ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<RestoreRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task MarkExpiredAsync(long restoreId, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE archive_restores SET status = 'expired', expired_utc = $now WHERE restore_id = $id;";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", restoreId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RestoreRecord Map(SqliteDataReader reader) => new()
    {
        RestoreId = reader.GetInt64(0),
        ArchiveId = reader.GetInt64(1),
        RequestedBy = reader.GetString(2),
        RequestedUtc = StorageFormat.ParseTimestamp(reader.GetString(3)),
        ExpiresUtc = StorageFormat.ParseTimestamp(reader.GetString(4)),
        EventCount = reader.GetInt32(5),
        Status = reader.GetString(6) == "active" ? RestoreStatus.Active : RestoreStatus.Expired,
        ExpiredUtc = reader.IsDBNull(7) ? null : StorageFormat.ParseTimestamp(reader.GetString(7)),
    };
}
