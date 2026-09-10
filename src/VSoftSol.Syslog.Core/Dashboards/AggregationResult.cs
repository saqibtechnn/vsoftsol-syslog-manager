namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// One cell of an aggregation result: the value for a (group, bucket) pair. For an
/// ungrouped aggregation <see cref="GroupKey"/> is null; for a non-bucketed one
/// <see cref="BucketIndex"/> is null. <see cref="GroupKey"/> is a category label that may be
/// derived from a hostname or app name straight off the wire — it is hostile input and must
/// be encoded at render (SECURITY_STANDARDS §5.2).
/// </summary>
public sealed record AggPoint(string? GroupKey, int? BucketIndex, double Value);

/// <summary>
/// The output of an aggregation query — the same shape for every visualization
/// (PHASE_09: one data path). <see cref="Points"/> is dense within the groups and buckets
/// that had data; the renderer fills gaps with zero using <see cref="Plan"/>.
/// </summary>
public sealed record AggregationResult
{
    public static AggregationResult Empty { get; } = new()
    {
        Points = [],
        Plan = null,
        Truncated = false,
        GroupsOmitted = 0,
    };

    public required IReadOnlyList<AggPoint> Points { get; init; }

    /// <summary>The bucket plan when the aggregation was time-bucketed; null otherwise.</summary>
    public BucketPlan? Plan { get; init; }

    /// <summary>
    /// True when the underlying scan hit the row cap and the numbers are a floor, not exact.
    /// The widget shows an "approximate" marker (UX_STANDARDS §4 — never make the user guess).
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>How many group values beyond <see cref="AggregationSpec.TopN"/> were folded away.</summary>
    public int GroupsOmitted { get; init; }

    public bool IsEmpty => Points.Count == 0;

    /// <summary>Distinct group keys in <see cref="Points"/>, in first-seen order.</summary>
    public IReadOnlyList<string?> GroupKeys()
    {
        var seen = new List<string?>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        bool nullSeen = false;
        foreach (AggPoint p in Points)
        {
            if (p.GroupKey is null)
            {
                if (!nullSeen)
                {
                    seen.Add(null);
                    nullSeen = true;
                }
            }
            else if (set.Add(p.GroupKey))
            {
                seen.Add(p.GroupKey);
            }
        }

        return seen;
    }

    /// <summary>The single scalar for a <c>Counter</c> / <c>RateGauge</c> — the sum of all points, or 0.</summary>
    public double Scalar()
    {
        double total = 0;
        foreach (AggPoint p in Points)
        {
            total += p.Value;
        }

        return total;
    }
}
