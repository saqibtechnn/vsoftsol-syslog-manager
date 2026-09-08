using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Devices;

/// <summary>Migration 004 — the Phase 6 device / discovery / stream schema.</summary>
public sealed class Migration004Tests
{
    [Fact]
    public async Task Migration004_AddsTheDeviceAndDiscoverySchema()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await TableExists(db, "device_ips")).Should().BeTrue();
        (await TableExists(db, "discovery_settings")).Should().BeTrue();
        (await ColumnExists(db, "devices", "approval_status")).Should().BeTrue();
        (await ColumnExists(db, "devices", "heartbeat_minutes")).Should().BeTrue();
        (await ColumnExists(db, "devices", "is_enabled")).Should().BeTrue();
        (await ColumnExists(db, "streams", "is_catch_all")).Should().BeTrue();
    }

    [Fact]
    public async Task Migration004_SeedsTheDiscoverySettingsRow()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT unknown_source_policy, max_pending_devices FROM discovery_settings WHERE id = 1;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("auto_register");
        reader.GetInt32(1).Should().Be(500);
    }

    [Fact]
    public async Task DeviceIps_IpIsUnique_SoDiscoveryCannotDoubleRegister()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, "INSERT INTO devices (name, created_utc) VALUES ('a','t'), ('b','t');");
        await Exec(db, "INSERT INTO device_ips (device_id, ip, is_primary, added_utc) VALUES (1, '10.0.0.9', 1, 't');");

        Func<Task> dup = () => Exec(db, "INSERT INTO device_ips (device_id, ip, is_primary, added_utc) VALUES (2, '10.0.0.9', 0, 't');");
        await dup.Should().ThrowAsync<SqliteException>();
    }

    private static async Task<bool> TableExists(SqliteTestDatabase db, string name)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ColumnExists(SqliteTestDatabase db, string table, string column)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
