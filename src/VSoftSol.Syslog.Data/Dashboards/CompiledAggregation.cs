using VSoftSol.Syslog.Data.Search;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// A ready-to-run aggregation query: one parameterised <c>SELECT … GROUP BY</c> over
/// <c>events</c>, with the bound parameters (the scoped search predicate's parameters plus
/// the group / value / bucket helpers). Nothing user-supplied is in <see cref="Sql"/> —
/// the group-by and value columns are resolved against <see cref="EventColumns"/>, and the
/// extracted-field names and bucket maths are bound (SECURITY_STANDARDS §5.1).
/// </summary>
internal sealed record CompiledAggregation
{
    public required string Sql { get; init; }

    public required IReadOnlyList<SearchParameter> Parameters { get; init; }

    /// <summary>True when the query groups by a distinct value of the group-by field.</summary>
    public bool Grouped { get; init; }

    /// <summary>True when the query buckets by time.</summary>
    public bool Bucketed { get; init; }

    /// <summary>Set when the compiler proved the query can match nothing (empty scope, bad field).</summary>
    public string? Rejected { get; init; }

    public static CompiledAggregation Reject(string reason) =>
        new() { Sql = string.Empty, Parameters = [], Rejected = reason };
}
