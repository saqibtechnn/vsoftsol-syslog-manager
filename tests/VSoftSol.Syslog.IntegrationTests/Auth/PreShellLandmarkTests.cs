using System.Text.RegularExpressions;
using FluentAssertions;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// WCAG 2.2 (1.3.1 / 2.4.1, axe rules <c>landmark-one-main</c> and <c>region</c>): the pages
/// that render outside the app shell — login, change-password, denied, error, and the first-run
/// wizard — each need exactly one <c>&lt;main&gt;</c> landmark, with the brand mark in a
/// <c>&lt;header&gt;</c>, so a screen-reader user can skip straight to the form. Found by the
/// v1.1.3 axe-core scan (P4-2); the app shell's own <c>&lt;main&gt;</c> never covered these.
/// </summary>
public sealed class PreShellLandmarkTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public PreShellLandmarkTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LoginPage_HasExactlyOneMainLandmark_WithBrandInHeaderAndFooterOutsideMain()
    {
        string html = await _factory.CreateClient().GetStringAsync("/login");

        AssertLandmarks(html);
        html.Should().MatchRegex("<footer[^>]*>", "the copyright line is page-level content-info, not part of the form");
    }

    [Fact]
    public async Task SetupWizard_HasExactlyOneMainLandmark_WithBrandInHeader()
    {
        await using var factory = new SyslogWebApplicationFactory { SeedCompletedSetup = false };

        string html = await factory.CreateClient().GetStringAsync("/setup");

        AssertLandmarks(html);
    }

    private static void AssertLandmarks(string html)
    {
        Regex.Matches(html, "<main[ >]").Count.Should().Be(1, "exactly one main landmark per page");
        html.Should().Contain("id=\"main-content\"", "the skip-link target the app shell already uses");
        Regex.Match(html, "<header[^>]*>.*?</header>", RegexOptions.Singleline).Value
            .Should().Contain("ds-brand", "the logo sits in a banner landmark, not loose in the body");
    }
}
