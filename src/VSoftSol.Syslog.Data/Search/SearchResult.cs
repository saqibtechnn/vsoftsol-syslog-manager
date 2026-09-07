using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// The outcome of <see cref="Scoping.ScopedEventReader.SearchAsync"/>: either an invalid
/// query (with the parser's message and position, for the query bar) or a page of results
/// with the total match count.
/// </summary>
public sealed record SearchResult
{
    public bool Ok { get; private init; }

    /// <summary>The query-language error, when <see cref="Ok"/> is false.</summary>
    public string? Error { get; private init; }

    /// <summary>Character offset of the error in the query text; -1 when not applicable.</summary>
    public int ErrorPosition { get; private init; } = -1;

    /// <summary>The result page (respecting the request's limit/offset/sort).</summary>
    public IReadOnlyList<SyslogEvent> Rows { get; private init; } = [];

    /// <summary>Total rows matching the query (ignoring limit/offset).</summary>
    public long TotalCount { get; private init; }

    /// <summary>True when <see cref="TotalCount"/> was capped at the exact-count ceiling.</summary>
    public bool TotalCountIsLowerBound { get; private init; }

    public static SearchResult Invalid(string error, int position) =>
        new() { Ok = false, Error = error, ErrorPosition = position };

    public static SearchResult Success(
        IReadOnlyList<SyslogEvent> rows, long totalCount, bool totalIsLowerBound) =>
        new() { Ok = true, Rows = rows, TotalCount = totalCount, TotalCountIsLowerBound = totalIsLowerBound };
}
