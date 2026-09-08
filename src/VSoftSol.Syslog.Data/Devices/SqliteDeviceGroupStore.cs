using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Devices;

/// <summary>
/// Device groups — used for RBAC scoping, filtering, dashboards, and bulk rule application
/// (PHASE_06 build item 4). A device may belong to many groups (<c>device_group_members</c>,
/// migration 001).
/// </summary>
public sealed class SqliteDeviceGroupStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<DeviceGroup>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.group_id, g.name, g.description, COUNT(m.device_id)
            FROM device_groups g
            LEFT JOIN device_group_members m ON m.group_id = g.group_id
            GROUP BY g.group_id, g.name, g.description
            ORDER BY g.name COLLATE NOCASE;
            """;

        var result = new List<DeviceGroup>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DeviceGroup
            {
                GroupId = reader.GetInt64(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                MemberCount = reader.GetInt32(3),
            });
        }

        return result;
    }

    public async Task<long> CreateAsync(string name, string? description, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO device_groups (name, description, created_utc) VALUES ($name, $desc, $now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<bool> RenameAsync(long groupId, string name, string? description, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE device_groups SET name = $name, description = $desc WHERE group_id = $id;";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", groupId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(long groupId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_groups WHERE group_id = $id;";
        command.Parameters.AddWithValue("$id", groupId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task SetMembersAsync(long groupId, IReadOnlyCollection<long> deviceIds, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (SqliteCommand clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM device_group_members WHERE group_id = $id;";
                clear.Parameters.AddWithValue("$id", groupId);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (long deviceId in deviceIds.Distinct())
            {
                await using SqliteCommand add = connection.CreateCommand();
                add.Transaction = transaction;
                add.CommandText = "INSERT OR IGNORE INTO device_group_members (group_id, device_id) VALUES ($gid, $did);";
                add.Parameters.AddWithValue("$gid", groupId);
                add.Parameters.AddWithValue("$did", deviceId);
                await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
