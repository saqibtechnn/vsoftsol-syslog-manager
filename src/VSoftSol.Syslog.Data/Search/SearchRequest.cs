using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// One search as the UI submits it: the free query text (parsed by the Phase 5 query
/// language) plus the structured filters the sidebar composes, and paging / sort. The
/// time range is mandatory — there is no unbounded query (PHASE_05 build item 2).
/// </summary>
public sealed record SearchRequest
{
    /// <summary>The query bar contents. Empty means "match everything in the time range".</summary>
    public string QueryText { get; init; } = string.Empty;

    /// <summary>Inclusive lower bound on <c>received_utc</c>. Required.</summary>
    public required DateTimeOffset FromUtc { get; init; }

    /// <summary>Exclusive upper bound on <c>received_utc</c>. Required.</summary>
    public required DateTimeOffset ToUtc { get; init; }

    /// <summary>Sidebar severity filter; empty means all. ANDed with the query.</summary>
    public IReadOnlyList<Severity> Severities { get; init; } = [];

    /// <summary>Sidebar device filter (device ids); empty means all. ANDed with the query.</summary>
    public IReadOnlyList<long> DeviceIds { get; init; } = [];

    /// <summary>Sidebar stream filter (stream ids); empty means all. ANDed with the query.</summary>
    public IReadOnlyList<long> StreamIds { get; init; } = [];

    /// <summary>Result ordering. Defaults to newest-first by receive time.</summary>
    public SearchSort Sort { get; init; } = SearchSort.Default;

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 1000;

    /// <summary>Rows to skip (grid paging / virtualization).</summary>
    public long Offset { get; init; }
}

/// <summary>
/// The column and direction a result grid is sorted by. The field is an enum, never a raw
/// string, so a sort parameter can never carry SQL or reach an out-of-scope column
/// (PHASE_05 security: "scope bypass via sort fields").
/// </summary>
public sealed record SearchSort(SearchSortField Field, bool Descending)
{
    public static SearchSort Default { get; } = new(SearchSortField.ReceivedUtc, Descending: true);
}

/// <summary>The allow-listed set of sortable grid columns.</summary>
public enum SearchSortField
{
    ReceivedUtc,
    EventUtc,
    Severity,
    Facility,
    Host,
    SourceIp,
    App,
    Vendor,
}
