using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// CRUD for <see cref="SavedSearch"/>. Every mutation re-checks ownership against the
/// requesting user id (PHASE_05 security — IDOR: "saved searches … accessed by another
/// user's ID"). A read returns a row only if the caller owns it or it is shared; a
/// non-owner never sees another user's private search, and cannot edit or delete a shared
/// one.
/// </summary>
public sealed class SqliteSavedSearchStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteSavedSearchStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The caller's own searches plus every shared search, ordered by name.</summary>
    public async Task<IReadOnlyList<SavedSearch>> ListForUserAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT saved_search_id, owner_user_id, name, query_text, time_range_json, is_shared, created_utc, updated_utc " +
            "FROM saved_searches WHERE owner_user_id = $uid OR is_shared = 1 ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$uid", userId);

        var result = new List<SavedSearch>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader, userId));
        }

        return result;
    }

    /// <summary>Fetch one search if the caller owns it or it is shared; otherwise null (no existence oracle).</summary>
    public async Task<SavedSearch?> GetAsync(long id, long requestingUserId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT saved_search_id, owner_user_id, name, query_text, time_range_json, is_shared, created_utc, updated_utc " +
            "FROM saved_searches WHERE saved_search_id = $id AND (owner_user_id = $uid OR is_shared = 1);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader, requestingUserId) : null;
    }

    /// <summary>
    /// The query text of a saved search by id, with no ownership check. A dashboard widget
    /// stores a saved-search reference, and a shared dashboard may be viewed by someone who
    /// does not own that search; the query text is not sensitive (it is a filter
    /// expression) and every result row the widget shows is still scope-filtered. Returns
    /// null if the search was deleted.
    /// </summary>
    public async Task<string?> GetQueryTextAsync(long id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT query_text FROM saved_searches WHERE saved_search_id = $id;";
        command.Parameters.AddWithValue("$id", id);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    public async Task<long> CreateAsync(
        long ownerUserId, string name, string queryText, string? timeRangeJson, bool isShared,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO saved_searches (owner_user_id, name, query_text, time_range_json, is_shared, created_utc, updated_utc) " +
            "VALUES ($uid, $name, $q, $tr, $shared, $now, $now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$uid", ownerUserId);
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$q", queryText ?? string.Empty);
        command.Parameters.AddWithValue("$tr", (object?)timeRangeJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$shared", isShared ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Updates a search only if <paramref name="requestingUserId"/> owns it. Returns false otherwise.</summary>
    public async Task<bool> UpdateAsync(
        long id, long requestingUserId, string name, string queryText, string? timeRangeJson, bool isShared,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE saved_searches SET name = $name, query_text = $q, time_range_json = $tr, is_shared = $shared, updated_utc = $now " +
            "WHERE saved_search_id = $id AND owner_user_id = $uid;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$q", queryText ?? string.Empty);
        command.Parameters.AddWithValue("$tr", (object?)timeRangeJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$shared", isShared ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Deletes a search only if <paramref name="requestingUserId"/> owns it.</summary>
    public async Task<bool> DeleteAsync(long id, long requestingUserId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM saved_searches WHERE saved_search_id = $id AND owner_user_id = $uid;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static SavedSearch Map(SqliteDataReader reader, long currentUserId)
    {
        long owner = reader.GetInt64(1);
        return new SavedSearch
        {
            Id = reader.GetInt64(0),
            OwnerUserId = owner,
            Name = reader.GetString(2),
            QueryText = reader.GetString(3),
            TimeRangeJson = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsShared = reader.GetInt64(5) == 1,
            OwnedByCurrentUser = owner == currentUserId,
            CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(6)),
            UpdatedUtc = StorageFormat.ParseTimestamp(reader.GetString(7)),
        };
    }
}
