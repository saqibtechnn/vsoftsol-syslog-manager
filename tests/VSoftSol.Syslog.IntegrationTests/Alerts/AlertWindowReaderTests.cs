using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// The hybrid window reader (ADR 0016). The headline test is the <b>differential</b>: the
/// SQL <c>GROUP BY</c> aggregate must produce exactly the counts an in-memory grouping of
/// the same rows produces — the two evaluation paths cannot diverge.
/// </summary>
public sealed class AlertWindowReaderTests
{
    private static SyslogEvent Event(string host, string ip, DateTimeOffset received, Severity sev = Severity.Warning) => new()
    {
        ReceivedUtc = received,
        SourceIp = ip,
        Hostname = host,
        Facility = Facility.Local0,
        Severity = sev,
        Protocol = Protocol.Udp,
        Message = $"msg from {host}",
        RawMessage = Encoding.UTF8.GetBytes("m"),
        ParseStatus = ParseStatus.Rfc3164,
    };

    [Fact]
    public async Task CountByGroup_AgreesWithInMemoryGrouping()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var rng = new Random(20260909);
        string[] hosts = ["core-sw-1", "core-sw-2", "edge-fw", "app-01"];
        DateTimeOffset t0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        var events = new List<SyslogEvent>();
        for (int i = 0; i < 400; i++)
        {
            events.Add(Event(hosts[rng.Next(hosts.Length)], $"10.0.0.{rng.Next(1, 6)}", t0.AddSeconds(rng.Next(0, 600))));
        }

        await db.Repository.AppendBatchAsync(events, CancellationToken.None);

        var reader = new SqliteAlertWindowReader(db.Factory);
        IReadOnlyList<GroupCount> sql = await reader.CountByGroupAsync(
            t0, t0.AddSeconds(600), "hostname", [], [], sampleSize: 3, CancellationToken.None);

        Dictionary<string, long> expected = events
            .GroupBy(e => e.Hostname!)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        sql.ToDictionary(g => g.GroupValue!, g => g.Count).Should().BeEquivalentTo(expected);
        sql.Should().OnlyContain(g => g.SampleEventIds.Count > 0 && g.SampleEventIds.Count <= 3);
    }

    [Fact]
    public async Task CountByGroup_Ungrouped_IsTheWholeWindowCount()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        DateTimeOffset t0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        await db.Repository.AppendBatchAsync(
            [Event("a", "1.1.1.1", t0), Event("b", "1.1.1.2", t0.AddMinutes(1)), Event("c", "1.1.1.3", t0.AddHours(2))],
            CancellationToken.None);

        var reader = new SqliteAlertWindowReader(db.Factory);
        IReadOnlyList<GroupCount> counts = await reader.CountByGroupAsync(
            t0, t0.AddMinutes(30), groupByField: null, [], [], 5, CancellationToken.None);

        counts.Should().ContainSingle();
        counts[0].GroupValue.Should().BeNull();
        counts[0].Count.Should().Be(2, "the third event is outside the window");
    }

    [Fact]
    public async Task DistinctCount_CountsDistinctColumnValues()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        DateTimeOffset t0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        await db.Repository.AppendBatchAsync(
            [
                Event("a", "10.0.0.1", t0), Event("b", "10.0.0.1", t0), Event("c", "10.0.0.2", t0),
                Event("d", "10.0.0.3", t0), Event("e", "10.0.0.4", t0),
            ],
            CancellationToken.None);

        var reader = new SqliteAlertWindowReader(db.Factory);
        (long distinct, IReadOnlyList<long> sample) = await reader.DistinctCountAsync(
            t0, t0.AddMinutes(5), "source_ip", [], [], 3, CancellationToken.None);

        distinct.Should().Be(4);
        sample.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StreamWindow_HonoursTheCap_AndOrder()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        DateTimeOffset t0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var events = Enumerable.Range(0, 50).Select(i => Event("h", "10.0.0.1", t0.AddSeconds(i))).ToList();
        await db.Repository.AppendBatchAsync(events, CancellationToken.None);

        var reader = new SqliteAlertWindowReader(db.Factory);
        var got = new List<SyslogEvent>();
        await foreach (SyslogEvent e in reader.StreamWindowAsync(t0, t0.AddMinutes(5), [], [], cap: 10, CancellationToken.None))
        {
            got.Add(e);
        }

        got.Should().HaveCount(10);
        got.Should().BeInAscendingOrder(e => e.ReceivedUtc);
    }

    [Fact]
    public async Task DeviceLastSeen_ReportsPerDeviceHeartbeatAndLatestEvent()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        DateTimeOffset t0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        long deviceId;
        await using (var c = await db.Factory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO devices (name, primary_ip, heartbeat_minutes, approval_status, is_enabled, created_utc)
                VALUES ('core-sw-1', '10.0.0.9', 30, 'approved', 1, 't');
                SELECT last_insert_rowid();
                """;
            deviceId = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        SyslogEvent e = Event("core-sw-1", "10.0.0.9", t0);
        e = e.WithRouting(deviceId, []);
        await db.Repository.AppendBatchAsync([e], CancellationToken.None);

        var reader = new SqliteAlertWindowReader(db.Factory);
        IReadOnlyList<DeviceSilence> silences = await reader.DeviceLastSeenAsync([], fallbackThresholdMinutes: 15, CancellationToken.None);

        DeviceSilence mine = silences.Single(s => s.DeviceName == "core-sw-1");
        mine.ThresholdMinutes.Should().Be(30, "the device's own heartbeat_minutes wins over the fallback");
        mine.LastSeenUtc.Should().BeCloseTo(t0, TimeSpan.FromSeconds(1));
    }
}
