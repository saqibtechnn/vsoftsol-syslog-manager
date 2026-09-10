using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

/// <summary>
/// PHASE_09 — time bucketing. Bucket boundaries are pure integer-second arithmetic from a
/// fixed origin, so a bucket that spans a DST change or midnight is still uniform width:
/// nothing is double-counted or dropped. The SQL side computes the identical index.
/// </summary>
public sealed class TimeBucketingTests
{
    [Theory]
    [InlineData(15, BucketInterval.OneMinute)]      // 15 min window → 1-minute buckets
    [InlineData(60 * 4, BucketInterval.FiveMinutes)] // 4 h → 5-minute (48 buckets)
    [InlineData(60 * 24, BucketInterval.FifteenMinutes)] // 24 h → 15-minute (96 buckets)
    [InlineData(60 * 24 * 7, BucketInterval.SixHours)]   // 7 d → 6-hour (28 buckets)
    [InlineData(60 * 24 * 30, BucketInterval.SixHours)]  // 30 d → 6-hour (120 buckets)
    [InlineData(60 * 24 * 365, BucketInterval.OneWeek)]  // 1 y → 1-week (52 buckets)
    public void AutoInterval_PicksTheLadderStepThatKeepsUnderTheTarget(int windowMinutes, BucketInterval expected)
    {
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddMinutes(windowMinutes);

        TimeBucketing.AutoInterval(from, to).Should().Be(expected);
    }

    [Fact]
    public void AutoInterval_ForAnEnormousWindow_CapsAtOneWeek()
    {
        var from = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        TimeBucketing.AutoInterval(from, to).Should().Be(BucketInterval.OneWeek);
    }

    [Fact]
    public void Plan_CoversTheWholeWindow_WithAtLeastOneBucket()
    {
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddHours(24);

        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneHour);

        plan.Count.Should().Be(24);
        plan.BucketSeconds.Should().Be(3600);
        plan.OriginUtc.Should().Be(from);
        plan.EndUtc.Should().Be(to);
        plan.BucketStartUtc(0).Should().Be(from);
        plan.BucketStartUtc(23).Should().Be(to.AddHours(-1));
    }

    [Fact]
    public void Plan_WithARaggedWindow_RoundsBucketCountUp()
    {
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddMinutes(150); // 2.5 hours

        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneHour);

        plan.Count.Should().Be(3);
        plan.EndUtc.Should().Be(from.AddHours(3));
    }

    [Fact]
    public void Plan_ClampsToMaxBuckets()
    {
        var from = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneMinute);

        plan.Count.Should().Be(TimeBucketing.MaxBuckets);
    }

    [Fact]
    public void Plan_RejectsNone()
    {
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        Action act = () => TimeBucketing.Plan(from, from.AddHours(1), BucketInterval.None);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IndexOf_IsExactAcrossTheUsDstSpringForward()
    {
        // 2026-03-08 07:00Z is 02:00 US Eastern → clocks jump to 03:00. In UTC nothing
        // special happens: buckets stay 1 hour of elapsed time.
        var from = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddHours(12);
        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneHour);

        var duringTransition = new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero);
        plan.IndexOf(duringTransition).Should().Be(7);
        plan.BucketStartUtc(7).Should().Be(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero));

        // Every bucket start is exactly 3600 s after the previous — no 23- or 25-hour day.
        for (int i = 1; i < plan.Count; i++)
        {
            (plan.BucketStartUtc(i) - plan.BucketStartUtc(i - 1)).TotalSeconds.Should().Be(3600);
        }
    }

    [Fact]
    public void IndexOf_IsExactAcrossTheUsDstFallBack()
    {
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddHours(12);
        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneHour);

        var duringTransition = new DateTimeOffset(2026, 11, 1, 6, 15, 0, TimeSpan.Zero);
        plan.IndexOf(duringTransition).Should().Be(6);
        for (int i = 1; i < plan.Count; i++)
        {
            (plan.BucketStartUtc(i) - plan.BucketStartUtc(i - 1)).TotalSeconds.Should().Be(3600);
        }
    }

    [Fact]
    public void Buckets_SumToTheWholeWindow_AcrossLeapDay()
    {
        var from = new DateTimeOffset(2028, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2028, 3, 1, 0, 0, 0, TimeSpan.Zero); // includes Feb 29
        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneHour);

        plan.Count.Should().Be(48);
        plan.BucketStartUtc(plan.Count).Should().Be(to);
        plan.IndexOf(new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.Zero)).Should().Be(36);
    }

    [Fact]
    public void Buckets_AreContiguous_AcrossAYearBoundary()
    {
        var from = new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);
        BucketPlan plan = TimeBucketing.Plan(from, to, BucketInterval.OneHour);

        plan.Count.Should().Be(24);
        plan.IndexOf(new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.Zero)).Should().Be(12);
    }

    [Fact]
    public void IndexOf_ForADeviceReportingInAnotherTimezone_MatchesTheUtcInstant()
    {
        // A device sends 2026-09-01T09:00+13:00 (Tonga). That is 2026-08-31T20:00Z.
        var from = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        BucketPlan plan = TimeBucketing.Plan(from, from.AddHours(30), BucketInterval.OneHour);

        var deviceLocal = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(13));
        plan.IndexOf(deviceLocal).Should().Be(20);
        plan.IndexOf(deviceLocal.ToUniversalTime()).Should().Be(20);
    }

    [Fact]
    public void IndexOf_BeforeTheOrigin_IsNegative()
    {
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        BucketPlan plan = TimeBucketing.Plan(from, from.AddHours(4), BucketInterval.OneHour);

        plan.IndexOf(from.AddSeconds(-1)).Should().Be(-1);
        plan.InRange(-1).Should().BeFalse();
        plan.InRange(0).Should().BeTrue();
        plan.InRange(4).Should().BeFalse();
    }
}
