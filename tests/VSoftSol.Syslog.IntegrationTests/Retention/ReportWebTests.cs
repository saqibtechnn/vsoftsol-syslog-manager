using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>
/// Real-HTTP regression test for <c>/reports/{id}/run</c> (v1.1): every "Run now" download
/// 500'd, because both <c>ReportAdminService.GetAsync</c> and
/// <c>ReportRenderService.RunNowAsync</c> resolved the acting user via
/// <c>CurrentUserAccessor</c>, which wraps Blazor Server's <c>ServerAuthenticationStateProvider</c>
/// — that throws when called from a plain minimal API endpoint handler, never a Razor
/// component's circuit (the "Run now" button in <c>Reports.razor</c> is a full page
/// navigation to this route, not a circuit-scoped call). Caught live in production, not by
/// this codebase's own test suite — no prior test exercised this route over real HTTP. Fixed
/// by giving both methods a way to take the caller's <c>ClaimsPrincipal</c> from
/// <c>HttpContext.User</c> directly. See ADR 0021's `UpdateAdminService.GetVerifiedDownloadAsync`
/// for the same fix pattern applied first.
/// </summary>
[Trait("Category", "Reports")]
public sealed class ReportWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public ReportWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private async Task<long> SeedReportAsync(long ownerUserId, string name = "Regression check")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SqliteReportStore>();
        var report = new ReportDefinition
        {
            Name = name,
            TemplateKey = ReportDefinition.CustomTemplateKey,
            QueryText = "",
            TimeRangeDays = 7,
        };
        return await store.CreateAsync(report, ownerUserId, isSystem: false, "test-setup", CancellationToken.None);
    }

    [Fact]
    public async Task RunNow_Administrator_Csv_Returns200_NotAServerError()
    {
        var admin = new WebAuthClient(_factory);
        string username = $"a-{Guid.NewGuid():N}";
        long userId = await admin.SeedUserAsync(username, "correct horse battery", Role.Administrator);
        await admin.LoginAsync(username, "correct horse battery");

        long reportId = await SeedReportAsync(userId);

        HttpResponseMessage response = await admin.Client.GetAsync($"/reports/{reportId}/run?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunNow_Unauthenticated_RedirectsToLogin_NotAServerError()
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync("/reports/1/run");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }
}
