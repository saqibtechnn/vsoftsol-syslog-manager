using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// A new device is held "pending" until an Administrator approves it, and until then the dashboards
/// said nothing — Device Health read "No devices are visible to you yet" and the first-run checklist
/// step stayed unticked while the Network Overview filled with that device's messages. A new admin
/// concluded nothing worked. The pending state must be visible on the dashboard itself.
/// </summary>
public sealed class PendingDeviceBannerTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public PendingDeviceBannerTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private static async Task<string> FirstDashboardAsync(WebAuthClient auth)
    {
        string list = await auth.Client.GetStringAsync("/dashboards");
        string path = Regex.Matches(list, @"href=""(/dashboards/\d+)""").Select(m => m.Groups[1].Value).First();
        return await auth.Client.GetStringAsync(path);
    }

    private static async Task<string> NetworkOverviewAsync(WebAuthClient auth)
    {
        string list = await auth.Client.GetStringAsync("/dashboards");
        foreach (string path in Regex.Matches(list, @"href=""(/dashboards/\d+)""").Select(m => m.Groups[1].Value).Distinct())
        {
            string page = await auth.Client.GetStringAsync(path);
            if (page.Contains("Message rate", StringComparison.Ordinal) && page.Contains("Top talkers", StringComparison.Ordinal))
            {
                return page;
            }
        }

        throw new InvalidOperationException("The Network Overview dashboard was not found.");
    }

    private async Task SeedPendingDeviceAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SqliteDeviceStore>();
        var settings = new DiscoverySettings { UnknownSourcePolicy = UnknownSourcePolicy.AutoRegister, MaxPendingDevices = 500 };
        await store.RegisterDiscoveredAsync("203.0.113.50", "new-firewall", settings, CancellationToken.None);
    }

    [Fact]
    public async Task Dashboard_ShowsAnApprovalBanner_OnlyWhileADeviceIsPending_AndRoutesEachRoleCorrectly()
    {
        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync("pend-admin", "pend-Test-Password-Long-1", Role.Administrator);
        var viewer = new WebAuthClient(_factory);
        await viewer.SignInAsync("pend-viewer", "pend-Test-Password-Long-1", Role.ReadOnly);

        (await FirstDashboardAsync(admin)).Should().NotContain("waiting for your approval", "no device is pending yet");

        await SeedPendingDeviceAsync();

        string asAdmin = await FirstDashboardAsync(admin);
        asAdmin.Should().Contain("waiting for your approval")
            .And.Contain("href=\"/devices/pending\"", "an Administrator is sent straight to the approval page");

        // The first-run checklist (on the Network Overview only) explains the unticked step in words,
        // and must render as text — a glued "@if" once leaked raw Razor code onto the page.
        string overview = await NetworkOverviewAsync(admin);
        overview.Should().Contain("it is waiting for approval").And.NotContain("@if").And.NotContain("_hasDevice");

        // Same data, different role: say who to ask instead of linking a page they cannot open.
        string asViewer = await FirstDashboardAsync(viewer);
        asViewer.Should().Contain("waiting for your approval").And.Contain("Ask an Administrator");
        asViewer.Should().NotContain("href=\"/devices/pending\"");
    }
}
