using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// Per-user grid column layouts. Every query and mutation is keyed by the requesting user
/// id — a user can never read, overwrite, or delete another user's layout (PHASE_05 IDOR).
/// </summary>
public sealed class SqliteColumnLayoutStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteColumnLayoutStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ColumnLayout>> ListForUserAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT layout_id, user_id, name, layout_json, is_default, created_utc, updated_utc " +
            "FROM user_column_layouts WHERE user_id = $uid ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$uid", userId);

        var result = new List<ColumnLayout>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<ColumnLayout?> GetDefaultAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT layout_id, user_id, name, layout_json, is_default, created_utc, updated_utc " +
            "FROM user_column_layouts WHERE user_id = $uid AND is_default = 1 LIMIT 1;";
        command.Parameters.AddWithValue("$uid", userId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Creates or replaces the layout named <paramref name="name"/> for this user.</summary>
    public async Task<long> SaveAsync(
        long userId, string name, string layoutJson, bool isDefault, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutJson);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (isDefault)
            {
                await using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "UPDATE user_column_layouts SET is_default = 0 WHERE user_id = $uid;";
                clear.Parameters.AddWithValue("$uid", userId);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                "INSERT INTO user_column_layouts (user_id, name, layout_json, is_default, created_utc, updated_utc) " +
                "VALUES ($uid, $name, $json, $def, $now, $now) " +
                "ON CONFLICT (user_id, name) DO UPDATE SET layout_json = $json, is_default = $def, updated_utc = $now; " +
                "SELECT layout_id FROM user_column_layouts WHERE user_id = $uid AND name = $name;";
            upsert.Parameters.AddWithValue("$uid", userId);
            upsert.Parameters.AddWithValue("$name", name.Trim());
            upsert.Parameters.AddWithValue("$json", layoutJson);
            upsert.Parameters.AddWithValue("$def", isDefault ? 1 : 0);
            upsert.Parameters.AddWithValue("$now", now);
            long id = Convert.ToInt64(
                await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(long id, long userId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_column_layouts WHERE layout_id = $id AND user_id = $uid;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", userId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static ColumnLayout Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        UserId = reader.GetInt64(1),
        Name = reader.GetString(2),
        LayoutJson = reader.GetString(3),
        IsDefault = reader.GetInt64(4) == 1,
        CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(5)),
        UpdatedUtc = StorageFormat.ParseTimestamp(reader.GetString(6)),
    };
}
