using FluentAssertions;
using VSoftSol.Syslog.Core.Reports;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Reports;

/// <summary>PHASE_10 — next-run calculation for scheduled reports. Pure UTC calendar
/// arithmetic, so it is exact across DST and month-length boundaries (no float subtraction
/// — see the sqlite-time-bucketing memory, the same class of correctness requirement).</summary>
public sealed class ReportScheduleTests
{
    [Fact]
    public void NextRunUtc_ForNone_IsNull()
    {
        ReportSchedule.None.NextRunUtc(DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void NextRunUtc_Daily_IsMidnightAtOrAfterNow()
    {
        var now = new DateTimeOffset(2026, 3, 15, 14, 30, 0, TimeSpan.Zero);

        DateTimeOffset? next = ReportSchedule.Daily.NextRunUtc(now);

        next.Should().Be(new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRunUtc_Daily_AtExactlyMidnight_ReturnsThatInstant()
    {
        var midnight = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        ReportSchedule.Daily.NextRunUtc(midnight).Should().Be(midnight);
    }

    [Fact]
    public void NextRunUtc_Weekly_IsTheNextMonday()
    {
        var wednesday = new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero); // a Wednesday
        DateTimeOffset? next = ReportSchedule.Weekly.NextRunUtc(wednesday);

        next!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Value.Should().BeAfter(wednesday);
        next.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void NextRunUtc_Weekly_OnAMondayMidnight_ReturnsThatInstant()
    {
        var monday = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero);
        ReportSchedule.Weekly.NextRunUtc(monday).Should().Be(monday);
    }

    [Fact]
    public void NextRunUtc_Monthly_IsTheFirstOfNextMonth_WhenPastTheFirst()
    {
        var mid = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        ReportSchedule.Monthly.NextRunUtc(mid).Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRunUtc_Monthly_AcrossAYearBoundary_RollsToJanuary()
    {
        var midDecember = new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero);
        ReportSchedule.Monthly.NextRunUtc(midDecember).Should().Be(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRunUtc_Monthly_FromJanuary31_LandsOnFebruary1_NotAnInvalidDate()
    {
        var jan31 = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        ReportSchedule.Monthly.NextRunUtc(jan31).Should().Be(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRunAfter_IsStrictlyAfterTheGivenRun_NotEqualToIt()
    {
        var lastRun = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? next = ReportSchedule.Daily.NextRunAfter(lastRun);

        next.Should().Be(new DateTimeOffset(2026, 3, 17, 0, 0, 0, TimeSpan.Zero));
    }
}
