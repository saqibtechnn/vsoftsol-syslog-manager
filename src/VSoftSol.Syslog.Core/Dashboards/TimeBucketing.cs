using System.Globalization;

namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// A resolved time-bucket plan: bucket 0 starts at <see cref="OriginUtc"/> (the window's
/// lower bound, exactly — no calendar snapping) and every bucket is exactly
/// <see cref="BucketSeconds"/> of elapsed time. Because the index is pure integer-second
/// arithmetic from a fixed origin, a bucket that straddles a DST transition or a midnight
/// is still uniform width — nothing is double-counted or dropped. The SQL side computes the
/// identical index with <c>(strftime('%s', received) - strftime('%s', origin)) / w</c>.
/// </summary>
public sealed record BucketPlan
{
    public required DateTimeOffset OriginUtc { get; init; }

    public required int BucketSeconds { get; init; }

    /// <summary>Number of buckets covering <c>[from, to)</c>. Always at least 1.</summary>
    public required int Count { get; init; }

    /// <summary>The interval this plan resolved from (never <see cref="BucketInterval.None"/> or <see cref="BucketInterval.Auto"/>).</summary>
    public required BucketInterval Interval { get; init; }

    /// <summary>The exclusive upper edge of the last bucket: <c>Origin + Count·BucketSeconds</c>.</summary>
    public DateTimeOffset EndUtc => OriginUtc.AddSeconds((long)Count * BucketSeconds);

    /// <summary>The inclusive lower edge (UTC instant) of bucket <paramref name="index"/>.</summary>
    public DateTimeOffset BucketStartUtc(int index) =>
        OriginUtc.AddSeconds((long)index * BucketSeconds);

    /// <summary>
    /// The bucket an instant falls in. Negative for an instant before <see cref="OriginUtc"/>;
    /// <see cref="Count"/> or greater for one at or after <see cref="EndUtc"/> — callers
    /// clamp or drop out-of-range points.
    /// </summary>
    public int IndexOf(DateTimeOffset instantUtc)
    {
        long delta = instantUtc.ToUnixTimeSeconds() - OriginUtc.ToUnixTimeSeconds();
        // Floor division so an instant one second before the origin lands in bucket -1,
        // not bucket 0.
        return (int)Math.Floor(delta / (double)BucketSeconds);
    }

    /// <summary>True when <paramref name="index"/> addresses a real bucket in this plan.</summary>
    public bool InRange(int index) => index >= 0 && index < Count;
}

/// <summary>
/// Pure bucket-plan maths for time-series widgets (PHASE_09 build item 2). No wall-clock,
/// no locale, no calendar arithmetic — every value is derived from the window bounds and a
/// whole number of seconds, so the plan is identical on every machine and across every DST
/// rule.
/// </summary>
public static class TimeBucketing
{
    /// <summary>Roughly how many points <see cref="BucketInterval.Auto"/> aims a series at.</summary>
    public const int TargetBucketCount = 150;

    /// <summary>Hard cap on buckets, so a huge window with a tiny interval cannot allocate unbounded arrays.</summary>
    public const int MaxBuckets = 500;

    /// <summary>
    /// Resolves <paramref name="interval"/> against the window and returns the plan.
    /// <see cref="BucketInterval.None"/> is rejected — a non-series aggregation has no plan.
    /// </summary>
    public static BucketPlan Plan(DateTimeOffset fromUtc, DateTimeOffset toUtc, BucketInterval interval)
    {
        if (interval == BucketInterval.None)
        {
            throw new ArgumentException("A non-bucketed aggregation has no plan.", nameof(interval));
        }

        BucketInterval resolved = interval == BucketInterval.Auto ? AutoInterval(fromUtc, toUtc) : interval;
        int bucketSeconds = resolved.Seconds();

        long span = Math.Max(1, toUtc.ToUnixTimeSeconds() - fromUtc.ToUnixTimeSeconds());
        int count = (int)Math.Min(MaxBuckets, Math.Max(1, (span + bucketSeconds - 1) / bucketSeconds));

        return new BucketPlan
        {
            OriginUtc = fromUtc,
            BucketSeconds = bucketSeconds,
            Count = count,
            Interval = resolved,
        };
    }

    /// <summary>
    /// The smallest ladder interval that keeps the window under
    /// <see cref="TargetBucketCount"/> buckets, or <see cref="BucketInterval.OneWeek"/> if
    /// even that is not enough.
    /// </summary>
    public static BucketInterval AutoInterval(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        long span = Math.Max(1, toUtc.ToUnixTimeSeconds() - fromUtc.ToUnixTimeSeconds());
        foreach (BucketInterval candidate in BucketIntervalExtensions.Ladder)
        {
            if (span / (double)candidate.Seconds() <= TargetBucketCount)
            {
                return candidate;
            }
        }

        return BucketInterval.OneWeek;
    }

    /// <summary>
    /// A short axis label for a bucket start, chosen by bucket size: time-of-day for
    /// sub-day buckets, date for day-and-up. Always formatted in
    /// <see cref="CultureInfo.InvariantCulture"/> against a caller-supplied local instant
    /// (the UI converts <see cref="BucketPlan.BucketStartUtc"/> to local first).
    /// </summary>
    public static string AxisLabel(DateTimeOffset localBucketStart, int bucketSeconds) =>
        bucketSeconds >= 24 * 60 * 60
            ? localBucketStart.ToString("MMM d", CultureInfo.InvariantCulture)
            : localBucketStart.ToString("HH:mm", CultureInfo.InvariantCulture);
}
