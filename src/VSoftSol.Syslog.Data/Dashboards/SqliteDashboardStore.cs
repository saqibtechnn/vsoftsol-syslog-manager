using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// CRUD for <see cref="DashboardDefinition"/> (PHASE_09). A read returns a row only if the
/// caller owns it, it is shared, or it is a shipped system dashboard; a non-owner cannot
/// edit or delete a shared one, and nobody can edit or delete a system one (IDOR — the
/// same discipline as <see cref="Search.SqliteSavedSearchStore"/>). The widgets and grid
/// layout live in JSON columns — a dashboard is saved as a whole.
/// </summary>
public sealed class SqliteDashboardStore
{
    private const string Columns =
        "dashboard_id, owner_user_id, name, description, is_shared, is_system, system_key, " +
        "default_range_seconds, refresh_seconds, widgets_json, layout_json, created_utc, updated_utc";

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;
    private long _version;

    public SqliteDashboardStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Bumped on every mutation, so a provider can rebuild a cache on change.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>System dashboards, the caller's own, and every shared dashboard — system first, then by name.</summary>
    public async Task<IReadOnlyList<DashboardDefinition>> ListForUserAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM dashboards " +
            "WHERE is_system = 1 OR owner_user_id = $uid OR is_shared = 1 " +
            "ORDER BY is_system DESC, name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$uid", userId);

        var result = new List<DashboardDefinition>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader, userId));
        }

        return result;
    }

    public async Task<DashboardDefinition?> GetAsync(long id, long requestingUserId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM dashboards " +
            "WHERE dashboard_id = $id AND (is_system = 1 OR owner_user_id = $uid OR is_shared = 1);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader, requestingUserId) : null;
    }

    public async Task<DashboardDefinition?> GetBySystemKeyAsync(string systemKey, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM dashboards WHERE system_key = $key;";
        command.Parameters.AddWithValue("$key", systemKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader, -1) : null;
    }

    public async Task<long> CreateAsync(
        DashboardDefinition dashboard, long ownerUserId, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dashboards
                (owner_user_id, name, description, is_shared, is_system, system_key,
                 default_range_seconds, refresh_seconds, widgets_json, layout_json, created_utc, updated_utc, updated_by)
            VALUES
                ($uid, $name, $desc, $shared, 0, NULL, $range, $refresh, $widgets, $layout, $now, $now, $by);
            SELECT last_insert_rowid();
            """;
        BindBody(command, dashboard);
        command.Parameters.AddWithValue("$uid", ownerUserId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);

        long id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        Interlocked.Increment(ref _version);
        return id;
    }

    /// <summary>Updates a dashboard only if the caller owns it and it is not a system dashboard.</summary>
    public async Task<bool> UpdateAsync(
        DashboardDefinition dashboard, long requestingUserId, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dashboards SET
                name = $name, description = $desc, is_shared = $shared,
                default_range_seconds = $range, refresh_seconds = $refresh,
                widgets_json = $widgets, layout_json = $layout, updated_utc = $now, updated_by = $by
            WHERE dashboard_id = $id AND owner_user_id = $uid AND is_system = 0;
            """;
        BindBody(command, dashboard);
        command.Parameters.AddWithValue("$id", dashboard.Id);
        command.Parameters.AddWithValue("$uid", requestingUserId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);

        bool updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (updated)
        {
            Interlocked.Increment(ref _version);
        }

        return updated;
    }

    /// <summary>Deletes a dashboard only if the caller owns it and it is not a system dashboard.</summary>
    public async Task<bool> DeleteAsync(long id, long requestingUserId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dashboards WHERE dashboard_id = $id AND owner_user_id = $uid AND is_system = 0;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);

        bool deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (deleted)
        {
            Interlocked.Increment(ref _version);
        }

        return deleted;
    }

    private static void BindBody(SqliteCommand command, DashboardDefinition dashboard)
    {
        command.Parameters.AddWithValue("$name", dashboard.Name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)dashboard.Description?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$shared", dashboard.IsShared ? 1 : 0);
        command.Parameters.AddWithValue("$range", Math.Max(60, (long)dashboard.DefaultTimeRange.TotalSeconds));
        command.Parameters.AddWithValue("$refresh", Math.Max(0, (long)dashboard.RefreshInterval.TotalSeconds));
        command.Parameters.AddWithValue("$widgets", DashboardJson.SerializeWidgets(dashboard.Widgets));
        command.Parameters.AddWithValue("$layout", DashboardJson.SerializeLayout(dashboard.Layout));
    }

    private static DashboardDefinition Map(SqliteDataReader reader, long currentUserId)
    {
        long? owner = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        return new DashboardDefinition
        {
            Id = reader.GetInt64(0),
            OwnerUserId = owner ?? 0,
            Name = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            IsShared = reader.GetInt64(4) == 1,
            IsSystem = reader.GetInt64(5) == 1,
            SystemKey = reader.IsDBNull(6) ? null : reader.GetString(6),
            DefaultTimeRange = TimeSpan.FromSeconds(reader.GetInt64(7)),
            RefreshInterval = TimeSpan.FromSeconds(reader.GetInt64(8)),
            Widgets = DashboardJson.DeserializeWidgets(reader.GetString(9)),
            Layout = DashboardJson.DeserializeLayout(reader.GetString(10)),
            OwnedByCurrentUser = owner is { } o && o == currentUserId,
            CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(11)),
            UpdatedUtc = StorageFormat.ParseTimestamp(reader.GetString(12)),
        };
    }
}
