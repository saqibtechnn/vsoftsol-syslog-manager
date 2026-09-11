namespace VSoftSol.Syslog.Core.Reports;

/// <summary>How often a report regenerates and delivers itself (PHASE_10 build item 8).</summary>
public enum ReportSchedule
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}

/// <summary>
/// Pure next-run calculation, all fixed at 00:00 UTC. All arithmetic is calendar-based
/// (<see cref="DateTimeOffset.AddDays(double)"/> / <see cref="DateTimeOffset.AddMonths(int)"/>),
/// never a fractional-day subtraction, so it is exact across DST and month-length
/// boundaries with no float rounding to worry about.
/// </summary>
public static class ReportScheduleExtensions
{
    /// <summary>The next scheduled run at-or-after <paramref name="afterUtc"/>, or null for
    /// <see cref="ReportSchedule.None"/>.</summary>
    public static DateTimeOffset? NextRunUtc(this ReportSchedule schedule, DateTimeOffset afterUtc) => schedule switch
    {
        ReportSchedule.Daily => NextDaily(afterUtc),
        ReportSchedule.Weekly => NextWeekly(afterUtc),
        ReportSchedule.Monthly => NextMonthly(afterUtc),
        _ => null,
    };

    /// <summary>The next scheduled run strictly after a completed run at <paramref name="lastRunUtc"/>.</summary>
    public static DateTimeOffset? NextRunAfter(this ReportSchedule schedule, DateTimeOffset lastRunUtc) =>
        schedule.NextRunUtc(lastRunUtc.UtcDateTime.AddTicks(1));

    private static DateTimeOffset NextDaily(DateTimeOffset afterUtc)
    {
        DateTimeOffset midnight = Midnight(afterUtc);
        return midnight >= afterUtc ? midnight : midnight.AddDays(1);
    }

    private static DateTimeOffset NextWeekly(DateTimeOffset afterUtc)
    {
        DateTimeOffset midnight = Midnight(afterUtc);
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)midnight.DayOfWeek + 7) % 7;
        DateTimeOffset candidate = midnight.AddDays(daysUntilMonday);
        return candidate >= afterUtc ? candidate : candidate.AddDays(7);
    }

    private static DateTimeOffset NextMonthly(DateTimeOffset afterUtc)
    {
        var firstOfMonth = new DateTimeOffset(afterUtc.Year, afterUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return firstOfMonth >= afterUtc ? firstOfMonth : firstOfMonth.AddMonths(1);
    }

    private static DateTimeOffset Midnight(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);
}
