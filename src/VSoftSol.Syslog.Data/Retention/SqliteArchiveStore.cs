using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>CRUD and status transitions for <see cref="ArchiveRecord"/> (PHASE_10 build item 4).</summary>
public sealed class SqliteArchiveStore
{
    private const string Columns =
        "archive_id, stream_id, stream_name, file_path, period_start_utc, period_end_utc, " +
        "event_count, byte_size, sha256, status, created_utc, verified_utc, deleted_utc";

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteArchiveStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<long> CreateOrReplaceAsync(ArchiveRecord archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO archives
                (stream_id, stream_name, file_path, period_start_utc, period_end_utc,
                 event_count, byte_size, sha256, status, created_utc)
            VALUES
                ($sid, $sname, $path, $start, $end, $count, $size, $hash, 'ok', $created)
            ON CONFLICT(file_path) DO UPDATE SET
                event_count = excluded.event_count, byte_size = excluded.byte_size,
                sha256 = excluded.sha256, status = 'ok', verified_utc = NULL, deleted_utc = NULL
            WHERE archives.status <> 'deleted';
            SELECT archive_id FROM archives WHERE file_path = $path;
            """;
        command.Parameters.AddWithValue("$sid", (object?)archive.StreamId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sname", archive.StreamName);
        command.Parameters.AddWithValue("$path", archive.FilePath);
        command.Parameters.AddWithValue("$start", StorageFormat.Timestamp(archive.PeriodStartUtc));
        command.Parameters.AddWithValue("$end", StorageFormat.Timestamp(archive.PeriodEndUtc));
        command.Parameters.AddWithValue("$count", archive.EventCount);
        command.Parameters.AddWithValue("$size", archive.ByteSize);
        command.Parameters.AddWithValue("$hash", archive.Sha256);
        command.Parameters.AddWithValue("$created", StorageFormat.Timestamp(_time.GetUtcNow()));

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<ArchiveRecord?> GetAsync(long archiveId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM archives WHERE archive_id = $id;";
        command.Parameters.AddWithValue("$id", archiveId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ArchiveRecord>> ListAsync(
        IReadOnlyCollection<long>? streamIdScope, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        string where = "status <> 'deleted'";
        if (streamIdScope is { Count: > 0 })
        {
            var names = new List<string>();
            int i = 0;
            foreach (long id in streamIdScope)
            {
                string name = $"$s{i++}";
                command.Parameters.AddWithValue(name, id);
                names.Add(name);
            }

            where += $" AND stream_id IN ({string.Join(", ", names)})";
        }

        command.CommandText = $"SELECT {Columns} FROM archives WHERE {where} ORDER BY period_start_utc DESC;";

        var result = new List<ArchiveRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    /// <summary>Archives not verified in the last <paramref name="staleAfter"/>, oldest first, capped.</summary>
    public async Task<IReadOnlyList<ArchiveRecord>> ListDueForVerificationAsync(
        TimeSpan staleAfter, int limit, CancellationToken cancellationToken)
    {
        string cutoff = StorageFormat.Timestamp(_time.GetUtcNow() - staleAfter);

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM archives
            WHERE status = 'ok' AND (verified_utc IS NULL OR verified_utc < $cutoff)
            ORDER BY COALESCE(verified_utc, created_utc) ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<ArchiveRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    /// <summary>Archives past their configured horizon (Delete tier) — 'ok' or
    /// 'tamper_detected', never re-purging one already 'deleted'.</summary>
    public async Task<IReadOnlyList<ArchiveRecord>> ListForPurgeAsync(long defaultColdDays, int limit, CancellationToken cancellationToken)
    {
        long nowEpoch = _time.GetUtcNow().ToUnixTimeSeconds();

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        // COALESCE wraps the WHOLE scalar subquery, not just its selected column: when
        // a.stream_id is NULL (an "unstreamed" archive) the correlated subquery matches
        // zero rows and itself evaluates to NULL, which would silently exclude the archive
        // from purge forever without the outer COALESCE.
        command.CommandText = $"""
            SELECT {Columns} FROM archives a
            WHERE a.status IN ('ok', 'tamper_detected', 'missing')
              AND CAST(strftime('%s', a.created_utc) AS INTEGER) <= $now - 86400 * COALESCE((
                    SELECT rp.cold_days FROM retention_policies rp WHERE rp.stream_id = a.stream_id), $default)
            ORDER BY a.created_utc ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", nowEpoch);
        command.Parameters.AddWithValue("$default", defaultColdDays);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<ArchiveRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public Task MarkVerifiedAsync(long archiveId, CancellationToken cancellationToken) =>
        SetStatusAsync(archiveId, "ok", verifiedNow: true, cancellationToken);

    public Task MarkTamperedAsync(long archiveId, CancellationToken cancellationToken) =>
        SetStatusAsync(archiveId, "tamper_detected", verifiedNow: true, cancellationToken);

    public Task MarkMissingAsync(long archiveId, CancellationToken cancellationToken) =>
        SetStatusAsync(archiveId, "missing", verifiedNow: true, cancellationToken);

    public async Task MarkDeletedAsync(long archiveId, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE archives SET status = 'deleted', deleted_utc = $now WHERE archive_id = $id;";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", archiveId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetStatusAsync(long archiveId, string status, bool verifiedNow, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = verifiedNow
            ? "UPDATE archives SET status = $status, verified_utc = $now WHERE archive_id = $id;"
            : "UPDATE archives SET status = $status WHERE archive_id = $id;";
        command.Parameters.AddWithValue("$status", status);
        if (verifiedNow)
        {
            command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        }

        command.Parameters.AddWithValue("$id", archiveId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ArchiveRecord Map(SqliteDataReader reader) => new()
    {
        ArchiveId = reader.GetInt64(0),
        StreamId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        StreamName = reader.GetString(2),
        FilePath = reader.GetString(3),
        PeriodStartUtc = StorageFormat.ParseTimestamp(reader.GetString(4)),
        PeriodEndUtc = StorageFormat.ParseTimestamp(reader.GetString(5)),
        EventCount = reader.GetInt32(6),
        ByteSize = reader.GetInt64(7),
        Sha256 = reader.GetString(8),
        Status = reader.GetString(9) switch
        {
            "ok" => ArchiveStatus.Ok,
            "tamper_detected" => ArchiveStatus.TamperDetected,
            "missing" => ArchiveStatus.Missing,
            _ => ArchiveStatus.Deleted,
        },
        CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(10)),
        VerifiedUtc = reader.IsDBNull(11) ? null : StorageFormat.ParseTimestamp(reader.GetString(11)),
        DeletedUtc = reader.IsDBNull(12) ? null : StorageFormat.ParseTimestamp(reader.GetString(12)),
    };
}
