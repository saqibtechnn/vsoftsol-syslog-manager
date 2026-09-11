using FluentAssertions;
using VSoftSol.Syslog.Core.Reports;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Reports;

/// <summary>PHASE_10 — report definition validation, including delivery config.</summary>
public sealed class ReportValidatorTests
{
    private static ReportDefinition ValidCanned() => new()
    {
        Name = "Monthly PCI report",
        TemplateKey = CannedReportCatalog.PciDss,
        TimeRangeDays = 90,
    };

    [Fact]
    public void Validate_ACannedReport_Succeeds()
    {
        ReportValidator.Validate(ValidCanned()).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithNoName_Fails()
    {
        ReportValidator.Validate(ValidCanned() with { Name = "" }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithATimeRangeOfZero_Fails()
    {
        ReportValidator.Validate(ValidCanned() with { TimeRangeDays = 0 }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithAnUnknownTemplateKey_Fails()
    {
        ReportValidator.Validate(ValidCanned() with { TemplateKey = "no-such-template" }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_ACustomReport_WithNoQueryAndNoSavedSearch_Fails()
    {
        var report = ValidCanned() with { TemplateKey = ReportDefinition.CustomTemplateKey, QueryText = null, SavedSearchId = null };
        ReportValidator.Validate(report).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_ACustomReport_WithAnInlineQuery_Succeeds()
    {
        var report = ValidCanned() with { TemplateKey = ReportDefinition.CustomTemplateKey, QueryText = "severity:error" };
        ReportValidator.Validate(report).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_ACustomReport_WithASavedSearchId_Succeeds()
    {
        var report = ValidCanned() with { TemplateKey = ReportDefinition.CustomTemplateKey, SavedSearchId = 7 };
        ReportValidator.Validate(report).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_AScheduledReport_WithNoDeliveryMethod_Fails()
    {
        var report = ValidCanned() with { Schedule = ReportSchedule.Monthly, Delivery = ReportDeliveryConfig.None };
        ReportValidator.Validate(report).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmailDelivery_WithNoRecipients_Fails()
    {
        var report = ValidCanned() with
        {
            Schedule = ReportSchedule.Monthly,
            Delivery = new ReportDeliveryConfig { Type = ReportDeliveryType.Email, Recipients = [] },
        };

        ReportValidator.Validate(report).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("has\r\nnewline@example.com")]
    public void Validate_EmailDelivery_WithAMalformedRecipient_Fails(string recipient)
    {
        var report = ValidCanned() with
        {
            Schedule = ReportSchedule.Monthly,
            Delivery = new ReportDeliveryConfig { Type = ReportDeliveryType.Email, Recipients = [recipient] },
        };

        ReportValidator.Validate(report).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmailDelivery_WithAValidRecipient_Succeeds()
    {
        var report = ValidCanned() with
        {
            Schedule = ReportSchedule.Monthly,
            Delivery = new ReportDeliveryConfig { Type = ReportDeliveryType.Email, Recipients = ["auditor@example.com"] },
        };

        ReportValidator.Validate(report).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_FolderDelivery_WithNoPath_Fails()
    {
        var report = ValidCanned() with
        {
            Schedule = ReportSchedule.Weekly,
            Delivery = new ReportDeliveryConfig { Type = ReportDeliveryType.Folder, FolderPath = null },
        };

        ReportValidator.Validate(report).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_FolderDelivery_WithAValidPath_Succeeds()
    {
        var report = ValidCanned() with
        {
            Schedule = ReportSchedule.Weekly,
            Delivery = new ReportDeliveryConfig { Type = ReportDeliveryType.Folder, FolderPath = @"\\fileserver\reports" },
        };

        ReportValidator.Validate(report).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_UnscheduledReport_IgnoresDeliveryConfig()
    {
        var report = ValidCanned() with { Schedule = ReportSchedule.None, Delivery = ReportDeliveryConfig.None };
        ReportValidator.Validate(report).Ok.Should().BeTrue();
    }
}
