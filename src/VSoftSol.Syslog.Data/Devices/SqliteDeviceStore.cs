using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Devices;

/// <summary>The result of resolving / discovering a source IP at ingest time.</summary>
public readonly record struct DeviceRegistration(long? DeviceId, bool CreatedPending, bool PendingQueueFull);

/// <summary>
/// The device registry (PHASE_06). Auto-discovery is idempotent: <see cref="RegisterDiscoveredAsync"/>
/// takes the process-wide write lock and gates on the unique <c>device_ips.ip</c> index, so
/// one unknown source IP produces exactly one pending record no matter how many messages
/// arrive or how many sources race. Every mutation is parameterised; approval is an
/// operation callers must gate behind the Administer policy.
/// </summary>
public sealed class SqliteDeviceStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;
    private long _floodDrops;

    public SqliteDeviceStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of discovery attempts dropped because the pending queue was at its cap.</summary>
    public long FloodDropCount => Interlocked.Read(ref _floodDrops);

    // ---------------------------------------------------------------- discovery (ingest path)

    public async Task<DeviceRegistration> RegisterDiscoveredAsync(
        string sourceIp, string? hostnameHint, DiscoverySettings settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIp);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        long? existing = await ResolveIpAsync(connection, sourceIp, cancellationToken).ConfigureAwait(false);
        if (existing is { } id)
        {
            await TouchAsync(connection, id, now, cancellationToken).ConfigureAwait(false);
            return new DeviceRegistration(id, CreatedPending: false, PendingQueueFull: false);
        }

        if (settings.UnknownSourcePolicy != UnknownSourcePolicy.AutoRegister)
        {
            return new DeviceRegistration(null, false, false);
        }

        long pending = await ScalarAsync(connection,
            "SELECT COUNT(*) FROM devices WHERE approval_status = 'pending';", cancellationToken).ConfigureAwait(false);
        if (pending >= settings.MaxPendingDevices)
        {
            Interlocked.Increment(ref _floodDrops);
            return new DeviceRegistration(null, false, PendingQueueFull: true);
        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long deviceId;
            await using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO devices (name, primary_ip, hostname, discovered, approval_status, created_utc, first_seen_utc, last_seen_utc)
                    VALUES ($name, $ip, $host, 1, 'pending', $now, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$name", DiscoveredName(sourceIp, hostnameHint));
                insert.Parameters.AddWithValue("$ip", sourceIp);
                insert.Parameters.AddWithValue("$host", (object?)hostnameHint ?? DBNull.Value);
                insert.Parameters.AddWithValue("$now", now);
                deviceId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            int linked;
            await using (SqliteCommand link = connection.CreateCommand())
            {
                link.Transaction = transaction;
                link.CommandText =
                    "INSERT OR IGNORE INTO device_ips (device_id, ip, is_primary, added_utc) VALUES ($id, $ip, 1, $now);";
                link.Parameters.AddWithValue("$id", deviceId);
                link.Parameters.AddWithValue("$ip", sourceIp);
                link.Parameters.AddWithValue("$now", now);
                linked = await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (linked == 0)
            {
                // Lost a race for this IP (only possible cross-process; the write lock serialises within one).
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                long? winner = await ResolveIpAsync(connection, sourceIp, cancellationToken).ConfigureAwait(false);
                return new DeviceRegistration(winner, false, false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DeviceRegistration(deviceId, CreatedPending: true, PendingQueueFull: false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Resolve a source IP to a device id, or null. Read-only, for the resolver cache to prime.</summary>
    public async Task<long?> ResolveIpAsync(string sourceIp, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveIpAsync(connection, sourceIp, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> ResolveIpAsync(SqliteConnection connection, string ip, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT device_id FROM device_ips WHERE ip = $ip;";
        command.Parameters.AddWithValue("$ip", ip);
        object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null || result is DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task TouchAsync(SqliteConnection connection, long deviceId, string now, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE devices SET last_seen_utc = $now, first_seen_utc = COALESCE(first_seen_utc, $now) WHERE device_id = $id;";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string DiscoveredName(string ip, string? hostname) =>
        string.IsNullOrWhiteSpace(hostname) ? ip : $"{hostname} ({ip})";

    // ---------------------------------------------------------------- queries

    public async Task<IReadOnlyList<Device>> ListAsync(
        IReadOnlyCollection<DeviceApprovalStatus>? statuses, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        string filter = string.Empty;
        if (statuses is { Count: > 0 })
        {
            var statusList = statuses.ToList();
            var names = new List<string>(statusList.Count);
            for (int i = 0; i < statusList.Count; i++)
            {
                string name = $"$s{i}";
                names.Add(name);
                command.Parameters.AddWithValue(name, statusList[i].ToString().ToLowerInvariant());
            }

            filter = $" WHERE approval_status IN ({string.Join(", ", names)})";
        }

        command.CommandText = SelectColumns + " FROM devices" + filter + " ORDER BY name COLLATE NOCASE;";

        var devices = new List<Device>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add(Map(reader));
        }

        await HydrateAsync(connection, devices, cancellationToken).ConfigureAwait(false);
        return devices;
    }

    public async Task<Device?> GetAsync(long deviceId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + " FROM devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        Device? device = null;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                device = Map(reader);
            }
        }

        if (device is null)
        {
            return null;
        }

        var list = new List<Device> { device };
        await HydrateAsync(connection, list, cancellationToken).ConfigureAwait(false);
        return list[0];
    }

    public Task<long> CountPendingAsync(CancellationToken cancellationToken) => CountByStatusAsync("pending", cancellationToken);

    private async Task<long> CountByStatusAsync(string status, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM devices WHERE approval_status = '{status}';", cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- mutations

    public async Task<long> CreateAsync(Device device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long id;
            await using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO devices
                      (name, hostname, primary_ip, vendor, model, role, site, owner, notes, timezone,
                       expected_msg_rate, heartbeat_minutes, is_enabled, discovered, approval_status, created_utc)
                    VALUES
                      ($name, $host, $ip, $vendor, $model, $role, $site, $owner, $notes, $tz,
                       $rate, $hb, $enabled, 0, 'approved', $now);
                    SELECT last_insert_rowid();
                    """;
                BindEditable(insert, device);
                insert.Parameters.AddWithValue("$now", now);
                id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await ReplaceIpsAsync(connection, transaction, id, EffectiveIps(device), now, cancellationToken).ConfigureAwait(false);
            await ReplaceGroupsAsync(connection, transaction, id, device.GroupIds, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> UpdateAsync(Device device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int rows;
            await using (SqliteCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE devices SET
                      name = $name, hostname = $host, primary_ip = $ip, vendor = $vendor, model = $model,
                      role = $role, site = $site, owner = $owner, notes = $notes, timezone = $tz,
                      expected_msg_rate = $rate, heartbeat_minutes = $hb, is_enabled = $enabled
                    WHERE device_id = $id;
                    """;
                BindEditable(update, device);
                update.Parameters.AddWithValue("$id", device.DeviceId);
                rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (rows == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await ReplaceIpsAsync(connection, transaction, device.DeviceId, EffectiveIps(device), now, cancellationToken).ConfigureAwait(false);
            await ReplaceGroupsAsync(connection, transaction, device.DeviceId, device.GroupIds, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> ApproveAsync(
        long deviceId, string name, string? vendor, string? role, int? heartbeatMinutes,
        IReadOnlyCollection<long> groupIds, string approvedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int rows;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE devices SET name = $name, vendor = $vendor, role = $role, heartbeat_minutes = $hb,
                      approval_status = 'approved', approved_utc = $now, approved_by = $by
                    WHERE device_id = $id AND approval_status = 'pending';
                    """;
                command.Parameters.AddWithValue("$name", name.Trim());
                command.Parameters.AddWithValue("$vendor", (object?)vendor ?? DBNull.Value);
                command.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
                command.Parameters.AddWithValue("$hb", (object?)heartbeatMinutes ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$by", approvedBy);
                command.Parameters.AddWithValue("$id", deviceId);
                rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (rows == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await ReplaceGroupsAsync(connection, transaction, deviceId, groupIds, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> RejectAsync(long deviceId, string rejectedBy, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET approval_status = 'rejected', is_enabled = 0, approved_utc = $now, approved_by = $by
            WHERE device_id = $id AND approval_status = 'pending';
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", rejectedBy);
        command.Parameters.AddWithValue("$id", deviceId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<long>> GetGroupIdsAsync(long deviceId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT group_id FROM device_group_members WHERE device_id = $id ORDER BY group_id;";
        command.Parameters.AddWithValue("$id", deviceId);
        var ids = new List<long>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    // ---------------------------------------------------------------- helpers

    private const string SelectColumns =
        "SELECT device_id, name, hostname, primary_ip, vendor, model, role, site, owner, notes, timezone, " +
        "expected_msg_rate, heartbeat_minutes, is_enabled, discovered, approval_status, first_seen_utc, last_seen_utc, created_utc";

    private static Device Map(SqliteDataReader r) => new()
    {
        DeviceId = r.GetInt64(0),
        Name = r.GetString(1),
        Hostname = r.IsDBNull(2) ? null : r.GetString(2),
        PrimaryIp = r.IsDBNull(3) ? null : r.GetString(3),
        Vendor = r.IsDBNull(4) ? null : r.GetString(4),
        Model = r.IsDBNull(5) ? null : r.GetString(5),
        Role = r.IsDBNull(6) ? null : r.GetString(6),
        Site = r.IsDBNull(7) ? null : r.GetString(7),
        Owner = r.IsDBNull(8) ? null : r.GetString(8),
        Notes = r.IsDBNull(9) ? null : r.GetString(9),
        Timezone = r.IsDBNull(10) ? null : r.GetString(10),
        ExpectedMessageRate = r.IsDBNull(11) ? null : r.GetInt32(11),
        HeartbeatMinutes = r.IsDBNull(12) ? null : r.GetInt32(12),
        IsEnabled = r.GetInt64(13) == 1,
        Discovered = r.GetInt64(14) == 1,
        ApprovalStatus = r.GetString(15) switch
        {
            "pending" => DeviceApprovalStatus.Pending,
            "rejected" => DeviceApprovalStatus.Rejected,
            _ => DeviceApprovalStatus.Approved,
        },
        FirstSeenUtc = r.IsDBNull(16) ? null : StorageFormat.ParseTimestamp(r.GetString(16)),
        LastSeenUtc = r.IsDBNull(17) ? null : StorageFormat.ParseTimestamp(r.GetString(17)),
        CreatedUtc = StorageFormat.ParseTimestamp(r.GetString(18)),
    };

    private static void BindEditable(SqliteCommand c, Device d)
    {
        c.Parameters.AddWithValue("$name", d.Name.Trim());
        c.Parameters.AddWithValue("$host", (object?)d.Hostname ?? DBNull.Value);
        c.Parameters.AddWithValue("$ip", (object?)(d.PrimaryIp ?? EffectiveIps(d).FirstOrDefault()) ?? DBNull.Value);
        c.Parameters.AddWithValue("$vendor", (object?)d.Vendor ?? DBNull.Value);
        c.Parameters.AddWithValue("$model", (object?)d.Model ?? DBNull.Value);
        c.Parameters.AddWithValue("$role", (object?)d.Role ?? DBNull.Value);
        c.Parameters.AddWithValue("$site", (object?)d.Site ?? DBNull.Value);
        c.Parameters.AddWithValue("$owner", (object?)d.Owner ?? DBNull.Value);
        c.Parameters.AddWithValue("$notes", (object?)d.Notes ?? DBNull.Value);
        c.Parameters.AddWithValue("$tz", (object?)d.Timezone ?? DBNull.Value);
        c.Parameters.AddWithValue("$rate", (object?)d.ExpectedMessageRate ?? DBNull.Value);
        c.Parameters.AddWithValue("$hb", (object?)d.HeartbeatMinutes ?? DBNull.Value);
        c.Parameters.AddWithValue("$enabled", d.IsEnabled ? 1 : 0);
    }

    private static List<string> EffectiveIps(Device d)
    {
        var set = new List<string>();
        void Add(string? ip)
        {
            if (!string.IsNullOrWhiteSpace(ip) && !set.Contains(ip, StringComparer.OrdinalIgnoreCase))
            {
                set.Add(ip.Trim());
            }
        }

        Add(d.PrimaryIp);
        foreach (string ip in d.Ips)
        {
            Add(ip);
        }

        return set;
    }

    private static async Task ReplaceIpsAsync(
        SqliteConnection connection, SqliteTransaction transaction, long deviceId,
        IReadOnlyList<string> ips, string now, CancellationToken ct)
    {
        await using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM device_ips WHERE device_id = $id;";
            clear.Parameters.AddWithValue("$id", deviceId);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (int i = 0; i < ips.Count; i++)
        {
            await using SqliteCommand add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText =
                "INSERT INTO device_ips (device_id, ip, is_primary, added_utc) VALUES ($id, $ip, $primary, $now);";
            add.Parameters.AddWithValue("$id", deviceId);
            add.Parameters.AddWithValue("$ip", ips[i]);
            add.Parameters.AddWithValue("$primary", i == 0 ? 1 : 0);
            add.Parameters.AddWithValue("$now", now);
            await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceGroupsAsync(
        SqliteConnection connection, SqliteTransaction transaction, long deviceId,
        IReadOnlyCollection<long> groupIds, CancellationToken ct)
    {
        await using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM device_group_members WHERE device_id = $id;";
            clear.Parameters.AddWithValue("$id", deviceId);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (long groupId in groupIds.Distinct())
        {
            await using SqliteCommand add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText =
                "INSERT OR IGNORE INTO device_group_members (group_id, device_id) VALUES ($gid, $did);";
            add.Parameters.AddWithValue("$gid", groupId);
            add.Parameters.AddWithValue("$did", deviceId);
            await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task HydrateAsync(SqliteConnection connection, List<Device> devices, CancellationToken ct)
    {
        if (devices.Count == 0)
        {
            return;
        }

        var ipMap = new Dictionary<long, List<string>>();
        var groupMap = new Dictionary<long, List<long>>();

        static void Add<T>(Dictionary<long, List<T>> map, long key, T value)
        {
            if (!map.TryGetValue(key, out List<T>? list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(value);
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT device_id, ip FROM device_ips ORDER BY is_primary DESC, ip;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Add(ipMap, reader.GetInt64(0), reader.GetString(1));
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT device_id, group_id FROM device_group_members;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Add(groupMap, reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        for (int i = 0; i < devices.Count; i++)
        {
            devices[i] = devices[i] with
            {
                Ips = ipMap.GetValueOrDefault(devices[i].DeviceId, []),
                GroupIds = groupMap.GetValueOrDefault(devices[i].DeviceId, []),
            };
        }
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
