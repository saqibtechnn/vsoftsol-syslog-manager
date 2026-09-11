using FluentAssertions;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 — <c>SqliteReportStore</c> CRUD, scheduling, IDOR discipline, and run bookkeeping.</summary>
public sealed class ReportStoreTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: true);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static ReportDefinition CustomReport(string name = "my report") => new()
    {
        Name = name,
        TemplateKey = ReportDefinition.CustomTemplateKey,
        QueryText = "severity:error",
        TimeRangeDays = 30,
    };

    [Fact]
    public async Task CreateAsync_ThenGetAsync_RoundTrips()
    {
        long id = await _h.Reports.CreateAsync(CustomReport(), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);

        ReportDefinition? fetched = await _h.Reports.GetAsync(id, requestingUserId: 1, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("my report");
        fetched.QueryText.Should().Be("severity:error");
    }

    [Fact]
    public async Task GetAsync_ByANonOwner_ReturnsNull_NotAnExistenceOracle()
    {
        long id = await _h.Reports.CreateAsync(CustomReport(), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);

        ReportDefinition? asOther = await _h.Reports.GetAsync(id, requestingUserId: 2, CancellationToken.None);

        asOther.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ByANonOwner_Fails()
    {
        long id = await _h.Reports.CreateAsync(CustomReport(), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);
        ReportDefinition owned = (await _h.Reports.GetAsync(id, 1, CancellationToken.None))!;

        bool updated = await _h.Reports.UpdateAsync(owned with { Name = "hijacked" }, requestingUserId: 2, "eve", CancellationToken.None);

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_OfASystemReport_Fails_EvenForTheOwnerColumn()
    {
        ReportDefinition system = (await _h.Reports.GetByTemplateKeyAsync(CannedReportCatalog.PciDss, CancellationToken.None))!;

        bool updated = await _h.Reports.UpdateAsync(system with { Name = "tampered" }, requestingUserId: 1, "alice", CancellationToken.None);

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ByANonOwner_Fails()
    {
        long id = await _h.Reports.CreateAsync(CustomReport(), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);

        bool deleted = await _h.Reports.DeleteAsync(id, requestingUserId: 2, CancellationToken.None);

        deleted.Should().BeFalse();
        (await _h.Reports.GetAsync(id, 1, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListForUserAsync_ReturnsSystemReportsForEveryone_AndOnlyOwnCustomReports()
    {
        long bobId = await _h.CreateUserAsync("bob");
        await _h.Reports.CreateAsync(CustomReport("mine"), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);
        await _h.Reports.CreateAsync(CustomReport("theirs"), ownerUserId: bobId, isSystem: false, "bob", CancellationToken.None);

        var forUser1 = await _h.Reports.ListForUserAsync(1, CancellationToken.None);

        forUser1.Should().Contain(r => r.Name == "mine");
        forUser1.Should().NotContain(r => r.Name == "theirs");
        forUser1.Count(r => r.IsSystem).Should().Be(11, "all 11 catalogue templates are seeded as system reports");
    }

    [Fact]
    public async Task CreateAsync_WithAMonthlySchedule_SetsNextRunUtc()
    {
        var scheduled = CustomReport() with
        {
            Schedule = ReportSchedule.Monthly,
            Delivery = new ReportDeliveryConfig { Type = ReportDeliveryType.Email, Recipients = ["a@b.com"] },
        };

        long id = await _h.Reports.CreateAsync(scheduled, ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);

        ReportDefinition fetched = (await _h.Reports.GetAsync(id, 1, CancellationToken.None))!;
        fetched.NextRunUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ListDueAsync_ReturnsOnlyReportsPastTheirNextRun()
    {
        var due = CustomReport("due") with { Schedule = ReportSchedule.Daily };
        long id = await _h.Reports.CreateAsync(due, ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);
        await _h.Reports.AdvanceNextRunAsync(id, _h.Clock.GetUtcNow().AddDays(-1), CancellationToken.None);

        var dueList = await _h.Reports.ListDueAsync(_h.Clock.GetUtcNow(), CancellationToken.None);

        dueList.Should().Contain(r => r.Id == id);
    }

    [Fact]
    public async Task RunLifecycle_StartCompleteDeliver_RecordsEverything()
    {
        long reportId = await _h.Reports.CreateAsync(CustomReport(), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);

        long runId = await _h.Reports.StartRunAsync(reportId, "alice", CancellationToken.None);
        await _h.Reports.CompleteRunAsync(runId, ReportRunStatus.Ok, rowCount: 42, pdfPath: "r.pdf", csvPath: "r.csv", error: null, CancellationToken.None);
        await _h.Reports.RecordDeliveryAsync(runId, ok: true, error: null, CancellationToken.None);

        var runs = await _h.Reports.ListRunsAsync(reportId, 10, CancellationToken.None);
        runs.Should().ContainSingle();
        runs[0].Status.Should().Be(ReportRunStatus.Ok);
        runs[0].RowCount.Should().Be(42);
        runs[0].DeliveredUtc.Should().NotBeNull();

        ReportDefinition report = (await _h.Reports.GetAsync(reportId, 1, CancellationToken.None))!;
        report.LastRunUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RunLifecycle_AFailedDelivery_RecordsTheError_WithoutClearingASuccessfulRun()
    {
        long reportId = await _h.Reports.CreateAsync(CustomReport(), ownerUserId: 1, isSystem: false, "alice", CancellationToken.None);
        long runId = await _h.Reports.StartRunAsync(reportId, "alice", CancellationToken.None);
        await _h.Reports.CompleteRunAsync(runId, ReportRunStatus.Ok, 1, "r.pdf", "r.csv", null, CancellationToken.None);

        await _h.Reports.RecordDeliveryAsync(runId, ok: false, error: "SMTP 550 mailbox unavailable", CancellationToken.None);

        var runs = await _h.Reports.ListRunsAsync(reportId, 10, CancellationToken.None);
        runs[0].Status.Should().Be(ReportRunStatus.Ok, "the report generated fine; only delivery failed");
        runs[0].DeliveryError.Should().Contain("550");
    }

    [Fact]
    public async Task SmtpSettings_SaveThenGet_RoundTrips()
    {
        await _h.SmtpSettings.SaveAsync(
            new VSoftSol.Syslog.Data.Reports.ReportSmtpSettings { Host = "smtp.example.com", Port = 25, FromAddress = "reports@example.com", UseTls = false },
            "alice", CancellationToken.None);

        var settings = await _h.SmtpSettings.GetAsync(CancellationToken.None);

        settings.Host.Should().Be("smtp.example.com");
        settings.Port.Should().Be(25);
        settings.IsConfigured.Should().BeTrue();
    }
}
