namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// The output of <see cref="SearchCompiler"/>: a parameterised SQL <c>WHERE</c> body and
/// <c>ORDER BY</c> clause, plus the bound parameter values. Not a full statement — the
/// executor wraps it with the column list, <c>FROM events e</c>, <c>LIMIT</c> and
/// <c>OFFSET</c>. Every user-supplied value is a parameter; nothing user-supplied is ever
/// concatenated into <see cref="WhereSql"/> (SECURITY_STANDARDS.md §5.1).
/// </summary>
public sealed record CompiledSearch
{
    /// <summary>
    /// The boolean expression for the SQL <c>WHERE</c> (without the <c>WHERE</c> keyword).
    /// Never empty in practice — the time-range bounds are always present.
    /// </summary>
    public required string WhereSql { get; init; }

    /// <summary>The <c>ORDER BY</c> clause (without the keyword), always ending with a tiebreak on <c>event_id</c>.</summary>
    public required string OrderBySql { get; init; }

    /// <summary>Parameter name → value, ready to bind. Names are compiler-generated (<c>$p0</c>, …).</summary>
    public required IReadOnlyList<SearchParameter> Parameters { get; init; }

    /// <summary>
    /// True when the compiler proved the query can match nothing (e.g. an empty scope
    /// intersection). The executor then returns an empty result without touching the DB.
    /// </summary>
    public bool MatchesNothing { get; init; }
}

/// <summary>A single bound SQL parameter.</summary>
public readonly record struct SearchParameter(string Name, object Value);
