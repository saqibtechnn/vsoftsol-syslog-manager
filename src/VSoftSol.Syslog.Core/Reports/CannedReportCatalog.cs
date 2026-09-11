using VSoftSol.Syslog.Core.Dashboards;

namespace VSoftSol.Syslog.Core.Reports;

/// <summary>
/// The fixed catalogue of report templates (PHASE_10 build item 6) — 7 canned operational
/// templates and 4 compliance templates, each a thin, clearly labelled lens over one of the
/// canned queries plus the specific control it evidences.
/// </summary>
public static class CannedReportCatalog
{
    public const string FailedAuthentication = "canned:failed-authentication";
    public const string ConfigurationChangeAudit = "canned:config-change-audit";
    public const string DeviceAvailability = "canned:device-availability";
    public const string InterfaceFlap = "canned:interface-flap";
    public const string SeverityTrend = "canned:severity-trend";
    public const string TopTalkers = "canned:top-talkers";
    public const string RuleAlertActivity = "canned:rule-alert-activity";

    public const string PciDss = "compliance:pci-dss";
    public const string Hipaa = "compliance:hipaa";
    public const string Iso27001 = "compliance:iso-27001";
    public const string Sox = "compliance:sox";

    public static IReadOnlyList<ReportTemplate> All { get; } = BuildAll();

    public static ReportTemplate? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    private static IReadOnlyList<ReportTemplate> BuildAll()
    {
        var failedAuth = new ReportTemplate
        {
            Key = FailedAuthentication,
            Name = "Failed Authentication Summary",
            Description = "Every message indicating a rejected login, authentication failure, or invalid credential.",
            // FTS5 has no stemmer (unicode61, exact tokens only) — "fail*" is a prefix
            // search so it matches "fail", "failed", "failure", "fails" alike; likewise
            // "auth*" covers "auth" and "authentication".
            QueryText = "(fail* OR denied OR invalid) AND (auth* OR login* OR password)",
        };

        var configAudit = new ReportTemplate
        {
            Key = ConfigurationChangeAudit,
            Name = "Configuration Change Audit",
            Description = "Every message indicating a device or system configuration was changed, saved, or reloaded.",
            QueryText = "config* AND (chang* OR modif* OR saved OR reload*)",
        };

        var deviceAvailability = new ReportTemplate
        {
            Key = DeviceAvailability,
            Name = "Device Availability",
            Description = "Message volume per device over the period — a silent device in a period it is expected to report is a gap worth investigating.",
            QueryText = "",
            Aggregation = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", Bucket = BucketInterval.None, TopN = 100 },
            DefaultTimeRangeDays = 30,
        };

        var interfaceFlap = new ReportTemplate
        {
            Key = InterfaceFlap,
            Name = "Interface Flap Report",
            Description = "Every message indicating a network interface changed link state.",
            QueryText = "interface* AND (up OR down OR flap*)",
        };

        var severityTrend = new ReportTemplate
        {
            Key = SeverityTrend,
            Name = "Severity Trend",
            Description = "Message volume by severity over time — a rising Error/Critical share is worth escalating before it becomes an incident.",
            QueryText = "",
            Aggregation = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "severity", Bucket = BucketInterval.Auto },
        };

        var topTalkers = new ReportTemplate
        {
            Key = TopTalkers,
            Name = "Top Talkers",
            Description = "The devices generating the most log volume over the period.",
            QueryText = "",
            Aggregation = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", Bucket = BucketInterval.None, TopN = 10 },
            DefaultTimeRangeDays = 30,
        };

        var ruleAlertActivity = new ReportTemplate
        {
            Key = RuleAlertActivity,
            Name = "Rule & Alert Activity",
            Description = "Every rule action and alert firing, acknowledgement, and resolution recorded in the audit trail.",
            Source = ReportSourceKind.AuditLog,
            AuditActionPrefixes = ["rule.", "action.", "alert."],
            DefaultTimeRangeDays = 30,
        };

        var canned = new[] { failedAuth, configAudit, deviceAvailability, interfaceFlap, severityTrend, topTalkers, ruleAlertActivity };

        var compliance = new[]
        {
            failedAuth with
            {
                Key = PciDss,
                Name = "PCI-DSS — Authentication Audit Trail",
                Description = "Evidences PCI-DSS Requirement 10.2.4/10.2.5: audit trails for invalid access attempts and the use of identification/authentication mechanisms.",
                Category = ReportTemplateCategory.Compliance,
                ControlReference = "PCI-DSS v4.0 Requirement 10.2.4 / 10.2.5",
            },
            ruleAlertActivity with
            {
                Key = Hipaa,
                Name = "HIPAA — Security Audit Trail",
                Description = "Evidences HIPAA Security Rule 45 CFR 164.312(b): hardware, software, and procedural mechanisms that record and examine activity in systems containing ePHI-adjacent infrastructure.",
                Category = ReportTemplateCategory.Compliance,
                ControlReference = "HIPAA Security Rule 45 CFR 164.312(b) — Audit controls",
            },
            severityTrend with
            {
                Key = Iso27001,
                Name = "ISO 27001 — Logging and Monitoring Evidence",
                Description = "Evidences ISO/IEC 27001:2022 Annex A 8.15: event logs are being generated and reviewed on an ongoing basis.",
                Category = ReportTemplateCategory.Compliance,
                ControlReference = "ISO/IEC 27001:2022 Annex A 8.15 — Logging",
            },
            configAudit with
            {
                Key = Sox,
                Name = "SOX — Change Control Evidence",
                Description = "Evidences Sarbanes-Oxley Section 404 change-control requirements: configuration changes to in-scope systems are logged.",
                Category = ReportTemplateCategory.Compliance,
                ControlReference = "SOX Section 404 — Change control / ITGC logging",
            },
        };

        return [.. canned, .. compliance];
    }
}
