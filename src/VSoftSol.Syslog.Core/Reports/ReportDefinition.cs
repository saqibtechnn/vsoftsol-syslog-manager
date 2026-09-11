namespace VSoftSol.Syslog.Core.Reports;

/// <summary>
/// A saved report (PHASE_10 build item 6) — a template instance or a fully custom query,
/// with an optional schedule and delivery. Persisted as a whole row (widgets/dashboards
/// precedent), not normalised across tables.
/// </summary>
public sealed record ReportDefinition
{
    public const string CustomTemplateKey = "custom";

    public long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>A <see cref="CannedReportCatalog"/> key, or <see cref="CustomTemplateKey"/>.</summary>
    public required string TemplateKey { get; init; }

    public long? SavedSearchId { get; init; }

    /// <summary>Inline query text for a custom report with no saved search. Ignored for
    /// canned/compliance templates — their query comes from the catalogue.</summary>
    public string? QueryText { get; init; }

    public int TimeRangeDays { get; init; } = 90;

    public ReportSchedule Schedule { get; init; } = ReportSchedule.None;

    public ReportDeliveryConfig Delivery { get; init; } = ReportDeliveryConfig.None;

    public long? OwnerUserId { get; init; }

    /// <summary>A compliance/canned template the operator has not customised — visible to
    /// everyone with report access, not owned, not deletable (PHASE_10 UX: "pick-and-run").</summary>
    public bool IsSystem { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }

    public string? UpdatedBy { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public DateTimeOffset? NextRunUtc { get; init; }

    public bool IsCustom => TemplateKey == CustomTemplateKey;
}
