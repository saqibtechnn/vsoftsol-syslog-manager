using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Streams;

/// <summary>A stream and its match rule.</summary>
public sealed record StreamRow
{
    public required long StreamId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public bool IsSystem { get; init; }

    public bool IsCatchAll { get; init; }

    public ConditionNode? Match { get; init; }

    public string? MatchJson { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// CRUD for streams (PHASE_06 build item 6). Every mutation bumps <see cref="Version"/> so
/// the ingest-path router knows to recompile. System streams (the seven defaults) keep
/// their name and catch-all flag but their match rule is editable.
/// </summary>
public sealed class SqliteStreamStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _version;

    /// <summary>Bumped on any change. The router recompiles when it observes a new value.</summary>
    public long Version => Interlocked.Read(ref _version);

    private const string Columns =
        "stream_id, name, description, enabled, is_system, is_catch_all, match_json, created_utc";

    private static object MatchParam(ConditionNode? match)
    {
        string json = StreamMatchJson.Serialize(match);
        return json.Length > 0 ? json : DBNull.Value;
    }

    public Task<IReadOnlyList<StreamRow>> ListActiveAsync(CancellationToken cancellationToken) =>
        QueryAsync("WHERE enabled = 1", null, cancellationToken);

    public async Task<IReadOnlyList<StreamRow>> ListAsync(UserScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllStreams)
        {
            return await QueryAsync(null, null, cancellationToken).ConfigureAwait(false);
        }

        var ids = scope.StreamIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var names = ids.Select((_, i) => $"$s{i}").ToList();
        void Bind(SqliteCommand c)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                c.Parameters.AddWithValue(names[i], ids[i]);
            }
        }

        return await QueryAsync($"WHERE stream_id IN ({string.Join(", ", names)})", Bind, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StreamRow?> GetAsync(long streamId, CancellationToken cancellationToken)
    {
        IReadOnlyList<StreamRow> rows = await QueryAsync(
            "WHERE stream_id = $id",
            c => c.Parameters.AddWithValue("$id", streamId),
            cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<IReadOnlyList<StreamRow>> QueryAsync(
        string? where, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM streams {where} ORDER BY sort_order, name COLLATE NOCASE;";
        bind?.Invoke(command);

        var result = new List<StreamRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string? json = reader.IsDBNull(6) ? null : reader.GetString(6);
            result.Add(new StreamRow
            {
                StreamId = reader.GetInt64(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Enabled = reader.GetInt64(3) == 1,
                IsSystem = reader.GetInt64(4) == 1,
                IsCatchAll = reader.GetInt64(5) == 1,
                MatchJson = json,
                Match = StreamMatchJson.Deserialize(json),
                CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(7)),
            });
        }

        return result;
    }

    public async Task<long> CreateAsync(
        string name, string? description, ConditionNode? match, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO streams (name, description, match_json, is_system, is_catch_all, enabled, sort_order, created_utc, updated_utc, updated_by)
            VALUES ($name, $desc, $match, 0, 0, 1,
                    (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM streams), $now, $now, $by);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$match", MatchParam(match));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);

        long id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        Interlocked.Increment(ref _version);
        return id;
    }

    public async Task<bool> UpdateAsync(
        long streamId, string name, string? description, bool enabled, ConditionNode? match,
        string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // System streams keep their identity (name, catch-all); only the rule and enabled state move.
        command.CommandText = """
            UPDATE streams SET
              name = CASE WHEN is_system = 1 THEN name ELSE $name END,
              description = $desc,
              enabled = $enabled,
              match_json = CASE WHEN is_catch_all = 1 THEN NULL ELSE $match END,
              updated_utc = $now, updated_by = $by
            WHERE stream_id = $id;
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$match", MatchParam(match));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        command.Parameters.AddWithValue("$id", streamId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    public async Task<bool> DeleteAsync(long streamId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM streams WHERE stream_id = $id AND is_system = 0;";
        command.Parameters.AddWithValue("$id", streamId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }
}
