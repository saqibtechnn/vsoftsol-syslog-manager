using System.Net;
using System.Text.Json;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

/// <summary>
/// Real-HTTP regression test for <c>/bundles/export</c> (v1.1): this endpoint 500'd on every
/// request because <c>ConfigBundleAdminService.ExportAsync</c> resolved the acting user via
/// <c>CurrentUserAccessor</c>, which wraps Blazor Server's <c>ServerAuthenticationStateProvider</c>
/// — that throws when called from a plain minimal API endpoint handler, never a Razor
/// component's circuit. Caught live in production, not by this codebase's own test suite —
/// <c>ConfigBundleTests</c> only exercises the exporter/importer classes directly, never this
/// route over real HTTP, which is exactly why the bug went unnoticed. Fixed by having
/// <c>ExportAsync</c> take the caller's <c>ClaimsPrincipal</c> from <c>HttpContext.User</c>
/// directly. See ADR 0021's `UpdateAdminService.GetVerifiedDownloadAsync` for the same fix
/// pattern applied first.
/// </summary>
[Trait("Category", "Bundles")]
public sealed class ConfigBundleWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public ConfigBundleWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Export_Administrator_Returns200_NotAServerError()
    {
        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        HttpResponseMessage response = await admin.Client.GetAsync("/bundles/export?title=RegressionCheck");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("RegressionCheck");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("DocumentJson", out _).Should().BeTrue("the export must be a real signed bundle, not an empty/error body");
    }

    [Fact]
    public async Task Export_Unauthenticated_RedirectsToLogin_NotAServerError()
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync("/bundles/export");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }
}
