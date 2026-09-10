namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// How a widget reduces its window of events to numbers (PHASE_09 build item 2): the
/// function, an optional value field for the arithmetic functions, an optional group-by
/// field, and an optional time bucket. Purely declarative — the compiler in
/// <c>VSoftSol.Syslog.Data</c> turns it into parameterised SQL, and
/// <see cref="AggregationValidator"/> rejects nonsensical combinations before it is stored.
/// </summary>
public sealed record AggregationSpec
{
    /// <summary>The reduction. <see cref="AggregationFunction.Count"/> by default.</summary>
    public AggregationFunction Function { get; init; } = AggregationFunction.Count;

    /// <summary>
    /// The field the arithmetic functions (<c>Sum</c>/<c>Average</c>/<c>Min</c>/<c>Max</c>)
    /// and <c>DistinctCount</c> operate on: a numeric built-in
    /// (<c>severity</c>, <c>facility</c>, <c>occurrence_count</c>) or an extracted
    /// <c>field.&lt;name&gt;</c>. Null for <see cref="AggregationFunction.Count"/>.
    /// </summary>
    public string? ValueField { get; init; }

    /// <summary>
    /// The field whose distinct values become series / bars / rows: a groupable built-in
    /// column or <c>field.&lt;name&gt;</c>. Null ⇒ a single ungrouped result.
    /// <c>message</c> is never groupable.
    /// </summary>
    public string? GroupByField { get; init; }

    /// <summary>The time bucket. <see cref="BucketInterval.None"/> ⇒ one value for the whole window.</summary>
    public BucketInterval Bucket { get; init; } = BucketInterval.None;

    /// <summary>
    /// For <c>Bar</c> / <c>TopNTable</c>: how many group values to keep (largest first).
    /// Clamped to <c>[1, 50]</c> by the validator. Ignored when <see cref="GroupByField"/>
    /// is null.
    /// </summary>
    public int TopN { get; init; } = 10;
}
