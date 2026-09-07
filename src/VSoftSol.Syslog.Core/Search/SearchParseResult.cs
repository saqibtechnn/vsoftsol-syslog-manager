namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// The outcome of parsing a search-query string. The parser never throws on user input
/// (TESTING_STANDARDS: malformed queries produce a clear user error, never an exception).
/// On failure it carries a plain-English message and the character offset it stopped at,
/// so the UI can point at the problem.
/// </summary>
public sealed record SearchParseResult
{
    private SearchParseResult(bool success, QueryNode? query, string? error, int errorPosition)
    {
        Success = success;
        Query = query;
        Error = error;
        ErrorPosition = errorPosition;
    }

    /// <summary>True when <see cref="Query"/> is populated.</summary>
    public bool Success { get; }

    /// <summary>The parsed AST, or null when <see cref="Success"/> is false.</summary>
    public QueryNode? Query { get; }

    /// <summary>A user-facing explanation of why the query is invalid, or null on success.</summary>
    public string? Error { get; }

    /// <summary>Zero-based character offset the parser stopped at. -1 when not applicable.</summary>
    public int ErrorPosition { get; }

    public static SearchParseResult Ok(QueryNode query) => new(true, query, null, -1);

    public static SearchParseResult Fail(string error, int errorPosition) =>
        new(false, null, error, errorPosition);
}
