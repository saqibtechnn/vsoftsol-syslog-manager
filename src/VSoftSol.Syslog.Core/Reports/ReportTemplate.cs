using VSoftSol.Syslog.Core.Dashboards;

namespace VSoftSol.Syslog.Core.Reports;

/// <summary>
/// A canned or compliance report template (PHASE_10 build item 6) — a fixed query/aggregation
/// pair, pick-and-run per UX_STANDARDS.md ("Report templates are pick-and-run, not
/// build-from-scratch"). Reuses <see cref="AggregationSpec"/> from the Phase 9 dashboard
/// framework for aggregate templates — the same "one data path" precedent extended to reports.
/// </summary>
public sealed record ReportTemplate
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public ReportTemplateCategory Category { get; init; } = ReportTemplateCategory.Canned;

    /// <summary>Non-null only for <see cref="ReportTemplateCategory.Compliance"/> — the
    /// specific control this template evidences, stated so it can be reviewed rather than
    /// merely named after the standard.</summary>
    public string? ControlReference { get; init; }

    public ReportSourceKind Source { get; init; } = ReportSourceKind.EventQuery;

    /// <summary>The Phase 5 search-query text, meaningful when <see cref="Source"/> is
    /// <see cref="ReportSourceKind.EventQuery"/>.</summary>
    public string QueryText { get; init; } = string.Empty;

    /// <summary>Audit action prefixes to include (e.g. "alert.", "action."), meaningful
    /// when <see cref="Source"/> is <see cref="ReportSourceKind.AuditLog"/>.</summary>
    public IReadOnlyList<string> AuditActionPrefixes { get; init; } = [];

    /// <summary>Null for a raw row-list report; set for an aggregated one (e.g. Severity Trend).</summary>
    public AggregationSpec? Aggregation { get; init; }

    public bool IsListReport => Aggregation is null;

    public int DefaultTimeRangeDays { get; init; } = 90;
}
