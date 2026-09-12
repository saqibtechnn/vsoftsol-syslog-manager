namespace VSoftSol.Syslog.Core.Reports;

/// <summary>One row of a list-shaped report (PHASE_10 build item 7).</summary>
public sealed record ReportEventRow(
    long EventId, DateTimeOffset ReceivedUtc, string Severity, string? Host, string? App, string Message, string? Vendor);

/// <summary>One row of an aggregate-shaped report (e.g. Severity Trend, Top Talkers).</summary>
public sealed record ReportAggregateRow(string? Group, string? BucketLabel, double Value);

/// <summary>One row of an audit-trail-shaped report (e.g. Rule &amp; Alert Activity).</summary>
public sealed record ReportAuditRow(DateTimeOffset OccurredUtc, string? Actor, string Action, string? EntityType, string? EntityId, string? Detail);

/// <summary>A time period whose events are archived (Cold, not currently restored) and so
/// are not represented in this report — the honest disclosure PHASE_10 build item 7 requires
/// ("whether archived data was included").</summary>
public sealed record ArchivedPeriod(DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// The fully resolved content a report renders from (PHASE_10 build item 7) — produced by
/// the Data-layer reader (which has scope and archive-awareness), consumed by the
/// PDF/CSV renderers, which do no querying of their own.
/// </summary>
public sealed record ReportContent
{
    public required string ReportName { get; init; }

    public required string TemplateKey { get; init; }

    public string? ControlReference { get; init; }

    public required string QueryText { get; init; }

    public required DateTimeOffset GeneratedUtc { get; init; }

    public required DateTimeOffset FromUtc { get; init; }

    public required DateTimeOffset ToUtc { get; init; }

    public required string GeneratingUser { get; init; }

    /// <summary>
    /// Set when the report's query could not run at all (a malformed custom query, or an
    /// aggregate template rejected by the compiler / excluded entirely by the viewer's
    /// scope) — P10-2 (`docs/evidence/phase-10/known-issues.md`). When set, every row list
    /// below is empty by construction; renderers must show this instead of a bare "no data"
    /// message, so a broken query is never indistinguishable from a genuinely empty result.
    /// </summary>
    public string? Error { get; init; }

    public IReadOnlyList<ReportEventRow> EventRows { get; init; } = [];

    public IReadOnlyList<ReportAggregateRow> AggregateRows { get; init; } = [];

    public IReadOnlyList<ReportAuditRow> AuditRows { get; init; } = [];

    public int RowCount { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<ArchivedPeriod> ArchivedPeriodsOmitted { get; init; } = [];

    public bool HasArchivedDataOmitted => ArchivedPeriodsOmitted.Count > 0;
}
