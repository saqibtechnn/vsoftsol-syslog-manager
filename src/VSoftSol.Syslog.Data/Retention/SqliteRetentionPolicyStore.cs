using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>CRUD for the global <see cref="RetentionSettings"/> row and per-stream
/// <see cref="RetentionPolicy"/> overrides (PHASE_10 build item 1).</summary>
public sealed class SqliteRetentionPolicyStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteRetentionPolicyStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<RetentionSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT default_hot_days, default_warm_days, default_cold_days, archive_root,
                   compression_level, batch_size, updated_utc, updated_by
            FROM retention_settings WHERE id = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new RetentionSettings();
        }

        return new RetentionSettings
        {
            DefaultHotDays = reader.GetInt32(0),
            DefaultWarmDays = reader.GetInt32(1),
            DefaultColdDays = reader.GetInt32(2),
            ArchiveRoot = reader.GetString(3),
            CompressionLevel = reader.GetInt32(4),
            BatchSize = reader.GetInt32(5),
            UpdatedUtc = reader.IsDBNull(6) ? null : StorageFormat.ParseTimestamp(reader.GetString(6)),
            UpdatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
        };
    }

    public async Task SaveSettingsAsync(RetentionSettings settings, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE retention_settings SET
                default_hot_days = $hot, default_warm_days = $warm, default_cold_days = $cold,
                archive_root = $root, compression_level = $level, batch_size = $batch,
                updated_utc = $now, updated_by = $by
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$hot", settings.DefaultHotDays);
        command.Parameters.AddWithValue("$warm", settings.DefaultWarmDays);
        command.Parameters.AddWithValue("$cold", settings.DefaultColdDays);
        command.Parameters.AddWithValue("$root", settings.ArchiveRoot);
        command.Parameters.AddWithValue("$level", settings.CompressionLevel);
        command.Parameters.AddWithValue("$batch", settings.BatchSize);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetentionPolicy>> ListPoliciesAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream_id, hot_days, warm_days, cold_days, archive_path, compression_level, updated_utc, updated_by
            FROM retention_policies ORDER BY stream_id;
            """;

        var result = new List<RetentionPolicy>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<RetentionPolicy?> GetPolicyAsync(long streamId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream_id, hot_days, warm_days, cold_days, archive_path, compression_level, updated_utc, updated_by
            FROM retention_policies WHERE stream_id = $id;
            """;
        command.Parameters.AddWithValue("$id", streamId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task SavePolicyAsync(RetentionPolicy policy, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.StreamId is not { } streamId)
        {
            throw new ArgumentException("A per-stream policy requires a StreamId.", nameof(policy));
        }

        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retention_policies (stream_id, hot_days, warm_days, cold_days, archive_path, compression_level, updated_utc, updated_by)
            VALUES ($id, $hot, $warm, $cold, $path, $level, $now, $by)
            ON CONFLICT(stream_id) DO UPDATE SET
                hot_days = excluded.hot_days, warm_days = excluded.warm_days, cold_days = excluded.cold_days,
                archive_path = excluded.archive_path, compression_level = excluded.compression_level,
                updated_utc = excluded.updated_utc, updated_by = excluded.updated_by;
            """;
        command.Parameters.AddWithValue("$id", streamId);
        command.Parameters.AddWithValue("$hot", policy.HotDays);
        command.Parameters.AddWithValue("$warm", policy.WarmDays);
        command.Parameters.AddWithValue("$cold", policy.ColdDays);
        command.Parameters.AddWithValue("$path", (object?)policy.ArchivePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$level", policy.CompressionLevel);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeletePolicyAsync(long streamId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM retention_policies WHERE stream_id = $id;";
        command.Parameters.AddWithValue("$id", streamId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static RetentionPolicy Map(SqliteDataReader reader) => new()
    {
        StreamId = reader.GetInt64(0),
        HotDays = reader.GetInt32(1),
        WarmDays = reader.GetInt32(2),
        ColdDays = reader.GetInt32(3),
        ArchivePath = reader.IsDBNull(4) ? null : reader.GetString(4),
        CompressionLevel = reader.GetInt32(5),
        UpdatedUtc = reader.IsDBNull(6) ? null : StorageFormat.ParseTimestamp(reader.GetString(6)),
        UpdatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
    };
}
