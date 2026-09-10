using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 time-bucketing matrix — the SQL bucket index (integer <c>strftime('%s')</c>
/// seconds) must agree with <see cref="TimeBucketing"/> across DST forward and back, leap
/// day, a year boundary, and a device sending in a different timezone. Assert no
/// double-counted or dropped buckets: Σ bucket counts == flat count, every time.
/// </summary>
public sealed class TimeBucketMatrixTests
{
    private static SyslogEvent At(DateTimeOffset when) => new()
    {
        ReceivedUtc = when.ToUniversalTime(),
        SourceIp = "10.0.0.1",
        Hostname = "h",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = "m",
        RawMessage = System.Text.Encoding.UTF8.GetBytes("m"),
        ParseStatus = ParseStatus.Rfc5424,
    };

    private static async Task<SqliteAggregationReader.AggregationOutcome> Bucketed(
        DashboardTestHarness h, DateTimeOffset from, DateTimeOffset to, BucketInterval bucket) =>
        await h.Aggregation.AggregateAsync(
            UserScope.Unrestricted, "", new AggregationSpec { Function = AggregationFunction.Count }, from, to, bucket, CancellationToken.None);

    [Theory]
    // DST spring-forward (US Eastern 2026-03-08 02:00 local == 07:00Z)
    [InlineData("2026-03-08T00:00:00Z", "2026-03-09T00:00:00Z")]
    // DST fall-back (US Eastern 2026-11-01 02:00 local == 06:00Z)
    [InlineData("2026-11-01T00:00:00Z", "2026-11-02T00:00:00Z")]
    // Leap day
    [InlineData("2028-02-28T00:00:00Z", "2028-03-01T00:00:00Z")]
    // Year boundary
    [InlineData("2026-12-31T00:00:00Z", "2027-01-02T00:00:00Z")]
    public async Task HourlyBuckets_PreserveEveryEvent(string fromIso, string toIso)
    {
        var from = DateTimeOffset.Parse(fromIso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        var to = DateTimeOffset.Parse(toIso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();

        await using DashboardTestHarness h = await DashboardTestHarness.CreateAsync(seed: false);

        // One event every 20 minutes across the window.
        var events = new List<SyslogEvent>();
        for (DateTimeOffset t = from; t < to; t = t.AddMinutes(20))
        {
            events.Add(At(t));
        }

        await h.SeedEventsAsync(events);

        SqliteAggregationReader.AggregationOutcome outcome = await Bucketed(h, from, to, BucketInterval.OneHour);

        BucketPlan plan = outcome.Result.Plan!;
        plan.BucketSeconds.Should().Be(3600);
        outcome.Result.Points.Sum(p => p.Value).Should().Be(events.Count, "Σ buckets == flat count");

        // No bucket index is out of range, and every populated bucket is contiguous maths.
        outcome.Result.Points.Should().OnlyContain(p => p.BucketIndex >= 0 && p.BucketIndex < plan.Count);
    }

    [Fact]
    public async Task ADeviceSendingInPlus13_LandsInTheBucketOfItsUtcInstant()
    {
        var from = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(30);
        await using DashboardTestHarness h = await DashboardTestHarness.CreateAsync(seed: false);

        // 2026-09-01T09:00+13:00 == 2026-08-31T20:00Z → hour bucket 20.
        var deviceLocal = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(13));
        await h.SeedEventsAsync([At(deviceLocal)]);

        SqliteAggregationReader.AggregationOutcome outcome = await Bucketed(h, from, to, BucketInterval.OneHour);

        outcome.Result.Points.Should().ContainSingle();
        outcome.Result.Points[0].BucketIndex.Should().Be(20);
        outcome.Result.Plan!.BucketStartUtc(20).Should().Be(new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task AnEventExactlyOnABucketBoundary_CountsOnceInTheLaterBucket()
    {
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(3);
        await using DashboardTestHarness h = await DashboardTestHarness.CreateAsync(seed: false);

        await h.SeedEventsAsync(
        [
            At(from),                       // bucket 0
            At(from.AddHours(1)),           // exactly the 0|1 boundary → bucket 1
            At(from.AddHours(1).AddTicks(-1)), // one tick before → bucket 0
        ]);

        SqliteAggregationReader.AggregationOutcome outcome = await Bucketed(h, from, to, BucketInterval.OneHour);

        outcome.Result.Points.Sum(p => p.Value).Should().Be(3);
        outcome.Result.Points.Where(p => p.BucketIndex == 0).Sum(p => p.Value).Should().Be(2);
        outcome.Result.Points.Where(p => p.BucketIndex == 1).Sum(p => p.Value).Should().Be(1);
    }
}
