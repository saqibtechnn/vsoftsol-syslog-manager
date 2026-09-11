namespace VSoftSol.Syslog.Core.Reports;

/// <summary>PHASE_10 build item 6: canned operational templates vs. compliance-mapped ones.</summary>
public enum ReportTemplateCategory
{
    Canned = 0,
    Compliance = 1,
    Custom = 2,
}

/// <summary>Where a report's rows come from (PHASE_10 build item 6). <see cref="AuditLog"/>
/// covers "Rule &amp; Alert Activity" — rule and alert firings live in the audit trail, not
/// in the event store.</summary>
public enum ReportSourceKind
{
    EventQuery = 0,
    AuditLog = 1,
}
