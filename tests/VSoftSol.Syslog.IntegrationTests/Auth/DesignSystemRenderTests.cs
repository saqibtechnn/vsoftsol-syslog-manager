using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 UX gate: the login and user-management screens are built entirely from the
/// design system (no one-off components). Asserted here by checking that every page
/// renders through the shared component classes and the branding pipeline.
/// </summary>
public sealed class DesignSystemRenderTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public DesignSystemRenderTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LoginPage_IsBuiltFromDesignSystemComponents()
    {
        string html = await _factory.CreateClient().GetStringAsync("/login");

        html.Should().Contain("ds-form")            // FormShell/EditForm
            .And.Contain("ds-field__label")         // FormField
            .And.Contain("ds-field__required")      // FormField required marker
            .And.Contain("ds-button--primary")      // shared button
            .And.Contain("ds-brand");               // BrandLogo (branding pipeline)
        html.Should().Contain("VSoftSol Syslog Manager", "branding strings come from BrandingInfo");
    }

    [Fact]
    public async Task UserManagementPage_UsesTheSharedTableAndEmptyState()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("ds-admin", "ds-Test-Password-Long-1", Role.Administrator);

        string html = await auth.Client.GetStringAsync("/settings/users");

        html.Should().Contain("ds-page__header");
        // Either the shared table (users exist) or the shared teaching empty state.
        (html.Contains("ds-table", StringComparison.Ordinal) || html.Contains("ds-empty", StringComparison.Ordinal))
            .Should().BeTrue();
        html.Should().Contain("ds-button--primary", "the 'Add user' action uses the shared button");
    }

    [Fact]
    public async Task AboutPage_ShowsVersionBuildDateVendorAndCopyright_FromBranding()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("ds-about", "ds-Test-Password-Long-1", Role.ReadOnly);

        string html = await auth.Client.GetStringAsync("/about");

        html.Should().Contain("1.1.2");
        html.Should().Contain("Vision Software Solutions");
        html.Should().Contain("vsoftsol.com");
        html.Should().Contain("&#xA9;").And.Contain("2026"); // copyright
    }

    [Fact]
    public async Task EveryPage_TitleFollowsThePageDashProductNameConvention()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("ds-title", "ds-Test-Password-Long-1", Role.Administrator);

        foreach ((string path, string expectedPrefix) in new[]
        {
            ("/about", "About — "),
            ("/settings/users", "Users"),
            ("/audit", "Audit log — "),
        })
        {
            string html = await auth.Client.GetStringAsync(path);
            html.Should().MatchRegex($"<title>{System.Text.RegularExpressions.Regex.Escape(expectedPrefix)}[^<]*VSoftSol Syslog Manager</title>");
        }
    }
}
