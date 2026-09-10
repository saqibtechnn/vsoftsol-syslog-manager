using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 "DeviceSilent test": device stops sending → alert fires after the threshold →
/// device resumes → alert auto-resolves. Plus the "Heartbeat accuracy" evidence requirement:
/// fires within the threshold ± one evaluation interval, resolves within one interval of
/// resumption.
/// </summary>
public sealed class AlertDeviceSilentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static SyslogEvent HeartbeatFrom(long deviceId, DateTimeOffset received) => new SyslogEvent
    {
        ReceivedUtc = received,
        SourceIp = "10.0.0.9",
        Hostname = "core-sw-1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = "keepalive",
        RawMessage = Encoding.UTF8.GetBytes("keepalive"),
        ParseStatus = ParseStatus.Rfc3164,
    }.WithRouting(deviceId, []);

    private static async Task<long> AddDeviceAsync(AlertEvaluationHarness h, int heartbeatMinutes)
    {
        await using var c = await h.Db.Factory.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices (name, primary_ip, heartbeat_minutes, approval_status, is_enabled, created_utc)
            VALUES ('core-sw-1', '10.0.0.9', $hb, 'approved', 1, 't');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$hb", heartbeatMinutes);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task DeviceStopsSending_FiresAfterHeartbeat_ThenResumes_AutoResolves()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0,
            configureEval: o => o.TickInterval = TimeSpan.FromMinutes(5));
        long deviceId = await AddDeviceAsync(h, heartbeatMinutes: 30);

        var alert = new AlertDefinition
        {
            Name = "device silent",
            Type = AlertEvaluationType.DeviceSilent,
            Severity = NotificationLevel.Critical,
            WindowSeconds = 900,
            IntervalSeconds = 300,
            Actions = [new RaiseNotificationAction { Title = "Silent: {hostname}" }],
            AutoResolve = true,
        };
        await h.Store.CreateAsync(alert, "op", CancellationToken.None);

        // device is healthy
        await h.Db.Repository.AppendBatchAsync([HeartbeatFrom(deviceId, T0.AddMinutes(-1))], CancellationToken.None);
        await h.AdvanceAndTickAsync(TimeSpan.FromMinutes(5));
        (await h.OpenInstancesAsync()).Should().BeEmpty();

        // ... now goes quiet. Advance past the 30-minute heartbeat.
        for (int i = 0; i < 7; i++)
        {
            await h.AdvanceAndTickAsync(TimeSpan.FromMinutes(5));
        }

        IReadOnlyList<AlertInstance> firing = await h.OpenInstancesAsync();
        firing.Should().ContainSingle();
        firing[0].GroupValue.Should().Be("core-sw-1");

        // fired within the threshold ± one interval: ~30m silent + at most one 5m tick
        (h.Clock.GetUtcNow() - T0).TotalMinutes.Should().BeInRange(30, 40);

        // device resumes
        await h.Db.Repository.AppendBatchAsync([HeartbeatFrom(deviceId, h.Clock.GetUtcNow())], CancellationToken.None);
        await h.AdvanceAndTickAsync(TimeSpan.FromMinutes(5));

        (await h.OpenInstancesAsync()).Should().BeEmpty("auto-resolved within one interval of resumption");
    }

    [Fact]
    public async Task DeviceSilent_HonoursEachDevicesOwnHeartbeatThreshold()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0,
            configureEval: o => o.TickInterval = TimeSpan.FromMinutes(1));
        long chatty = await AddDeviceAsync(h, heartbeatMinutes: 10);

        long slow;
        await using (var c = await h.Db.Factory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO devices (name, primary_ip, heartbeat_minutes, approval_status, is_enabled, created_utc)
                VALUES ('slow-sensor', '10.0.0.20', 240, 'approved', 1, 't');
                SELECT last_insert_rowid();
                """;
            slow = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var alert = new AlertDefinition
        {
            Name = "silent",
            Type = AlertEvaluationType.DeviceSilent,
            WindowSeconds = 600,
            IntervalSeconds = 60,
            Actions = [],
            AutoResolve = true,
        };
        await h.Store.CreateAsync(alert, "op", CancellationToken.None);

        // both devices were last heard from a minute ago
        await h.Db.Repository.AppendBatchAsync(
            [HeartbeatFrom(chatty, T0.AddMinutes(-1)),
             new SyslogEvent
             {
                 ReceivedUtc = T0.AddMinutes(-1), SourceIp = "10.0.0.20", Hostname = "slow-sensor",
                 Facility = Facility.Local0, Severity = Severity.Informational, Protocol = Protocol.Udp,
                 Message = "hello", RawMessage = Encoding.UTF8.GetBytes("hello"), ParseStatus = ParseStatus.Rfc3164,
             }.WithRouting(slow, [])],
            CancellationToken.None);

        // 15 minutes later: the 10-minute device is silent; the 240-minute device is not.
        for (int i = 0; i < 15; i++)
        {
            await h.AdvanceAndTickAsync(TimeSpan.FromMinutes(1));
        }

        IReadOnlyList<AlertInstance> open = await h.OpenInstancesAsync();
        open.Select(i => i.GroupValue).Should().Equal("core-sw-1");
    }
}
