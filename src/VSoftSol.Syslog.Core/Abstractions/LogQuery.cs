using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.Abstractions;

/// <summary>
/// A resolved, structured log query. The Phase 5 query language compiles to this; the
/// UI filter sidebar composes it directly. Kept deliberately small — a seam contract,
/// not the query engine.
/// </summary>
public sealed record LogQuery
{
    /// <summary>Inclusive lower bound on <c>received_utc</c>.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Exclusive upper bound on <c>received_utc</c>.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Restrict to these severities; empty means all.</summary>
    public IReadOnlyList<Severity> Severities { get; init; } = [];

    /// <summary>Restrict to these device ids; empty means all in scope.</summary>
    public IReadOnlyList<long> DeviceIds { get; init; } = [];

    /// <summary>Restrict to these stream ids; empty means all in scope.</summary>
    public IReadOnlyList<long> StreamIds { get; init; } = [];

    /// <summary>Full-text match against the message body and raw text; null means no text filter.</summary>
    public string? FullText { get; init; }

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 1000;

    /// <summary>Rows to skip (paging).</summary>
    public long Offset { get; init; }

    /// <summary>When true, order newest-first; otherwise oldest-first.</summary>
    public bool Descending { get; init; } = true;
}
