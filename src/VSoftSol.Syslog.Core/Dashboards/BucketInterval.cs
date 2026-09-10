namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// The time-bucket size for a time-series aggregation. <see cref="None"/> means "one number
/// for the whole window" (non-series visualizations). <see cref="Auto"/> asks
/// <see cref="TimeBucketing.AutoInterval"/> to pick a size from the fixed ladder so a
/// series lands near <see cref="TimeBucketing.TargetBucketCount"/> points regardless of the
/// window. Every named size is a whole number of seconds — bucket boundaries are computed
/// on integer unix-second arithmetic, never on <c>julianday()</c> floats
/// (see the "sqlite-time-bucketing" note), so DST transitions and midnight never split or
/// double-count a bucket.
/// </summary>
public enum BucketInterval
{
    None,
    Auto,
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    OneHour,
    SixHours,
    OneDay,
    OneWeek,
}

/// <summary>Seconds-per-bucket for each named <see cref="BucketInterval"/>.</summary>
public static class BucketIntervalExtensions
{
    /// <summary>
    /// The fixed ladder <see cref="BucketInterval.Auto"/> chooses from, smallest first.
    /// </summary>
    public static IReadOnlyList<BucketInterval> Ladder { get; } =
    [
        BucketInterval.OneMinute,
        BucketInterval.FiveMinutes,
        BucketInterval.FifteenMinutes,
        BucketInterval.OneHour,
        BucketInterval.SixHours,
        BucketInterval.OneDay,
        BucketInterval.OneWeek,
    ];

    /// <summary>
    /// The whole number of seconds in one bucket of this interval. Throws for
    /// <see cref="BucketInterval.None"/> and <see cref="BucketInterval.Auto"/> — resolve
    /// those through <see cref="TimeBucketing"/> first.
    /// </summary>
    public static int Seconds(this BucketInterval interval) => interval switch
    {
        BucketInterval.OneMinute => 60,
        BucketInterval.FiveMinutes => 5 * 60,
        BucketInterval.FifteenMinutes => 15 * 60,
        BucketInterval.OneHour => 60 * 60,
        BucketInterval.SixHours => 6 * 60 * 60,
        BucketInterval.OneDay => 24 * 60 * 60,
        BucketInterval.OneWeek => 7 * 24 * 60 * 60,
        _ => throw new ArgumentOutOfRangeException(
            nameof(interval), interval, "None / Auto have no fixed second count — resolve via TimeBucketing."),
    };

    /// <summary>A short human label ("5m", "1h", "1d").</summary>
    public static string ShortLabel(this BucketInterval interval) => interval switch
    {
        BucketInterval.OneMinute => "1m",
        BucketInterval.FiveMinutes => "5m",
        BucketInterval.FifteenMinutes => "15m",
        BucketInterval.OneHour => "1h",
        BucketInterval.SixHours => "6h",
        BucketInterval.OneDay => "1d",
        BucketInterval.OneWeek => "1w",
        BucketInterval.Auto => "auto",
        _ => "—",
    };
}
