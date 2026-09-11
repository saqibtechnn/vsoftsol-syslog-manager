using VSoftSol.Syslog.Core.Reports;

namespace VSoftSol.Syslog.Data.Seed;

/// <summary>The 11 catalogue reports, seeded as <c>is_system</c> rows so they are pick-and-run
/// from day one (PHASE_10 build item 6; UX_STANDARDS.md "no blank slates").</summary>
public static class DefaultReports
{
    public static IReadOnlyList<ReportDefinition> All { get; } =
    [
        .. CannedReportCatalog.All.Select(t => new ReportDefinition
        {
            Name = t.Name,
            TemplateKey = t.Key,
            TimeRangeDays = t.DefaultTimeRangeDays,
            Schedule = ReportSchedule.None,
            Delivery = ReportDeliveryConfig.None,
            IsSystem = true,
            Enabled = true,
        }),
    ];
}
