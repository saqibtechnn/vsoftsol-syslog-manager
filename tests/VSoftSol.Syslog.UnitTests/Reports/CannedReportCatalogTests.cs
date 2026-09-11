using FluentAssertions;
using VSoftSol.Syslog.Core.Reports;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Reports;

/// <summary>PHASE_10 — the fixed 7 canned + 4 compliance template catalogue.</summary>
public sealed class CannedReportCatalogTests
{
    [Fact]
    public void All_ContainsExactlySevenCannedAndFourComplianceTemplates()
    {
        CannedReportCatalog.All.Count(t => t.Category == ReportTemplateCategory.Canned).Should().Be(7);
        CannedReportCatalog.All.Count(t => t.Category == ReportTemplateCategory.Compliance).Should().Be(4);
    }

    [Fact]
    public void NoTemplate_UsesABareWildcardForMatchAll()
    {
        // The Phase 5 query grammar rejects a bare "*" ("needs at least one character
        // before it" — it is a prefix-wildcard operator, not a match-all token). The
        // product's match-all convention is an empty query string. A live bug once shipped
        // three of these as literal "*" and was only caught by an integration test that
        // actually executed the aggregation — this unit test catches it at the source.
        foreach (ReportTemplate template in CannedReportCatalog.All.Where(t => t.Source == ReportSourceKind.EventQuery))
        {
            template.QueryText.Should().NotBe("*", $"'{template.Key}' would fail to parse — use an empty string for match-all");
        }
    }

    [Fact]
    public void All_HasNoDuplicateKeys()
    {
        CannedReportCatalog.All.Select(t => t.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryComplianceTemplate_StatesTheControlItEvidences()
    {
        foreach (var template in CannedReportCatalog.All.Where(t => t.Category == ReportTemplateCategory.Compliance))
        {
            template.ControlReference.Should().NotBeNullOrWhiteSpace(
                $"'{template.Key}' is a compliance template and must cite the control it evidences");
        }
    }

    [Fact]
    public void CannedTemplates_HaveNoControlReference()
    {
        foreach (var template in CannedReportCatalog.All.Where(t => t.Category == ReportTemplateCategory.Canned))
        {
            template.ControlReference.Should().BeNull();
        }
    }

    [Fact]
    public void EveryTemplate_HasANonEmptyNameAndDescription()
    {
        foreach (var template in CannedReportCatalog.All)
        {
            template.Name.Should().NotBeNullOrWhiteSpace();
            template.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData(CannedReportCatalog.FailedAuthentication)]
    [InlineData(CannedReportCatalog.ConfigurationChangeAudit)]
    [InlineData(CannedReportCatalog.DeviceAvailability)]
    [InlineData(CannedReportCatalog.InterfaceFlap)]
    [InlineData(CannedReportCatalog.SeverityTrend)]
    [InlineData(CannedReportCatalog.TopTalkers)]
    [InlineData(CannedReportCatalog.RuleAlertActivity)]
    [InlineData(CannedReportCatalog.PciDss)]
    [InlineData(CannedReportCatalog.Hipaa)]
    [InlineData(CannedReportCatalog.Iso27001)]
    [InlineData(CannedReportCatalog.Sox)]
    public void Find_ResolvesEveryDeclaredKey(string key)
    {
        CannedReportCatalog.Find(key).Should().NotBeNull();
    }

    [Fact]
    public void Find_OfAnUnknownKey_ReturnsNull()
    {
        CannedReportCatalog.Find("no-such-template").Should().BeNull();
    }

    [Fact]
    public void AggregateTemplates_AreNotListReports_AndViceVersa()
    {
        ReportTemplate severityTrend = CannedReportCatalog.Find(CannedReportCatalog.SeverityTrend)!;
        ReportTemplate failedAuth = CannedReportCatalog.Find(CannedReportCatalog.FailedAuthentication)!;

        severityTrend.IsListReport.Should().BeFalse();
        failedAuth.IsListReport.Should().BeTrue();
    }

    [Fact]
    public void RuleAlertActivity_SourcesFromTheAuditLog_NotEventQuery()
    {
        ReportTemplate template = CannedReportCatalog.Find(CannedReportCatalog.RuleAlertActivity)!;
        template.Source.Should().Be(ReportSourceKind.AuditLog);
        template.AuditActionPrefixes.Should().NotBeEmpty();
    }
}
