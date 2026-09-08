using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Devices;

/// <summary>
/// PHASE_06 discovery: an unknown source creates exactly one pending record (not one per
/// message), concurrent sources do not collide, and a discovery flood is contained.
/// </summary>
public sealed class DeviceDiscoveryTests
{
    private static readonly DiscoverySettings AutoRegister = new()
    {
        UnknownSourcePolicy = UnknownSourcePolicy.AutoRegister,
        MaxPendingDevices = 500,
    };

    [Fact]
    public async Task RegisterDiscoveredAsync_ManyMessagesFromOneSource_CreatesExactlyOnePendingRecord()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDeviceStore(db.Factory);

        var first = await store.RegisterDiscoveredAsync("203.0.113.5", "edge-fw", AutoRegister, CancellationToken.None);
        first.CreatedPending.Should().BeTrue();

        for (int i = 0; i < 5_000; i++)
        {
            var again = await store.RegisterDiscoveredAsync("203.0.113.5", "edge-fw", AutoRegister, CancellationToken.None);
            again.DeviceId.Should().Be(first.DeviceId);
            again.CreatedPending.Should().BeFalse();
        }

        (await Count(db, "SELECT COUNT(*) FROM devices WHERE approval_status = 'pending';")).Should().Be(1);
        (await Count(db, "SELECT COUNT(*) FROM device_ips WHERE ip = '203.0.113.5';")).Should().Be(1);
    }

    [Fact]
    public async Task RegisterDiscoveredAsync_TwentyConcurrentSources_ProduceTwentyDistinctDevices_NoDuplicates()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDeviceStore(db.Factory);

        // Each source fires 50 concurrent registrations for its own IP.
        var tasks = new List<Task>();
        for (int s = 0; s < 20; s++)
        {
            string ip = $"198.51.100.{s + 1}";
            for (int r = 0; r < 50; r++)
            {
                tasks.Add(store.RegisterDiscoveredAsync(ip, null, AutoRegister, CancellationToken.None));
            }
        }

        await Task.WhenAll(tasks);

        (await Count(db, "SELECT COUNT(*) FROM devices WHERE approval_status = 'pending';")).Should().Be(20);
        (await Count(db, "SELECT COUNT(DISTINCT ip) FROM device_ips;")).Should().Be(20);
        (await Count(db, "SELECT COUNT(*) FROM device_ips;")).Should().Be(20, "the unique ip index prevents any duplicate mapping");
    }

    [Fact]
    public async Task RegisterDiscoveredAsync_FloodOfSpoofedIps_StopsAtTheCap_AndCountsTheDrops()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDeviceStore(db.Factory);
        var settings = new DiscoverySettings { UnknownSourcePolicy = UnknownSourcePolicy.AutoRegister, MaxPendingDevices = 50 };

        int created = 0;
        int capped = 0;
        for (int i = 0; i < 5_000; i++)
        {
            var result = await store.RegisterDiscoveredAsync($"10.{i / 256 % 256}.{i % 256}.7", null, settings, CancellationToken.None);
            if (result.CreatedPending)
            {
                created++;
            }

            if (result.PendingQueueFull)
            {
                capped++;
            }
        }

        created.Should().Be(50, "the pending queue is bounded by MaxPendingDevices");
        capped.Should().BeGreaterThan(0);
        store.FloodDropCount.Should().BeGreaterThan(0);
        (await Count(db, "SELECT COUNT(*) FROM devices;")).Should().Be(50, "the database does not balloon during a flood");
    }

    [Fact]
    public async Task Resolver_HitsTheDatabaseOncePerNewIp_ThenServesFromCache()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await SetPolicy(db, "auto_register", 500);
        var store = new SqliteDeviceStore(db.Factory);
        var settings = new SqliteDiscoverySettingsStore(db.Factory);
        var resolver = new DeviceResolver(store, settings, NullLogger<DeviceResolver>.Instance);

        long? id = null;
        for (int i = 0; i < 100_000; i++)
        {
            id = await resolver.ResolveAsync("192.0.2.50", "host-a", CancellationToken.None);
        }

        id.Should().NotBeNull();
        (await Count(db, "SELECT COUNT(*) FROM devices WHERE approval_status = 'pending';")).Should().Be(1);
    }

    [Fact]
    public async Task Resolver_PausesDiscoveryOnceTheQueueIsFull_KeepingTheFloodOffTheDatabase()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await SetPolicy(db, "auto_register", 20);
        var store = new SqliteDeviceStore(db.Factory);
        var settings = new SqliteDiscoverySettingsStore(db.Factory);
        var resolver = new DeviceResolver(store, settings, NullLogger<DeviceResolver>.Instance);

        for (int i = 0; i < 10_000; i++)
        {
            await resolver.ResolveAsync($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", null, CancellationToken.None);
        }

        resolver.FloodEventCount.Should().BeGreaterThan(0);
        (await Count(db, "SELECT COUNT(*) FROM devices;")).Should().BeLessThanOrEqualTo(21);
    }

    [Fact]
    public async Task RegisterDiscoveredAsync_WithRejectPolicy_CreatesNothing()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteDeviceStore(db.Factory);
        var reject = new DiscoverySettings { UnknownSourcePolicy = UnknownSourcePolicy.Reject, MaxPendingDevices = 500 };

        var result = await store.RegisterDiscoveredAsync("203.0.113.99", null, reject, CancellationToken.None);

        result.DeviceId.Should().BeNull();
        result.CreatedPending.Should().BeFalse();
        (await Count(db, "SELECT COUNT(*) FROM devices;")).Should().Be(0);
    }

    private static async Task<long> Count(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SetPolicy(SqliteTestDatabase db, string policy, int max)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE discovery_settings SET unknown_source_policy = $p, max_pending_devices = $m WHERE id = 1;";
        cmd.Parameters.AddWithValue("$p", policy);
        cmd.Parameters.AddWithValue("$m", max);
        await cmd.ExecuteNonQueryAsync();
    }
}
