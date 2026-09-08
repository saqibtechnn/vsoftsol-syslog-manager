using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Devices;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Devices;

/// <summary>
/// PHASE_06 web surface + security: route authorization, approval is Administrator-only
/// <b>at the service</b> (not just hidden in the UI), and hostile device fields from the
/// wire render encoded.
/// </summary>
public sealed class DeviceWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public DeviceWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static ClaimsPrincipal PrincipalFor(Role role, string name = "tester")
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role.ToString())],
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private DeviceAdminService AdminAs(Role role)
    {
        IServiceProvider sp = _factory.Services;
        return new DeviceAdminService(
            sp.GetRequiredService<SqliteDeviceStore>(),
            sp.GetRequiredService<SqliteDeviceGroupStore>(),
            sp.GetRequiredService<SqliteDiscoverySettingsStore>(),
            sp.GetRequiredService<DeviceResolver>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            new CurrentUserAccessor(new FakeAuthState(PrincipalFor(role))));
    }

    private async Task<long> SeedPendingAsync(string hostname = "edge-fw", string ip = "203.0.113.200")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SqliteDeviceStore>();
        var settings = new DiscoverySettings { UnknownSourcePolicy = UnknownSourcePolicy.AutoRegister, MaxPendingDevices = 500 };
        DeviceRegistration reg = await store.RegisterDiscoveredAsync(ip, hostname, settings, CancellationToken.None);
        return reg.DeviceId!.Value;
    }

    [Theory]
    [InlineData("/devices")]
    [InlineData("/devices/pending")]
    [InlineData("/streams")]
    [InlineData("/streams/tester")]
    public async Task Routes_RequireAuthentication(string path)
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task PendingDevices_RequiresAdministrator()
    {
        var operatorClient = new WebAuthClient(_factory);
        await operatorClient.SignInAsync("op6", "correct horse battery", Role.Operator);
        HttpResponseMessage denied = await operatorClient.Client.GetAsync("/devices/pending");
        denied.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden);

        var adminClient = new WebAuthClient(_factory);
        await adminClient.SignInAsync("admin6", "correct horse battery", Role.Administrator);
        HttpResponseMessage allowed = await adminClient.Client.GetAsync("/devices/pending");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Approve_IsRefusedForOperatorAndReadOnly_AtTheService()
    {
        long id = await SeedPendingAsync("switch-a", "203.0.113.201");

        DeviceActionResult asOperator = await AdminAs(Role.Operator)
            .ApproveAsync(id, "Switch A", "Cisco", "switch", null, [], CancellationToken.None);
        asOperator.Ok.Should().BeFalse();
        asOperator.Message.Should().ContainEquivalentOf("administrator");

        DeviceActionResult asReadOnly = await AdminAs(Role.ReadOnly)
            .ApproveAsync(id, "Switch A", "Cisco", "switch", null, [], CancellationToken.None);
        asReadOnly.Ok.Should().BeFalse();

        // still pending — nothing changed
        using IServiceScope scope = _factory.Services.CreateScope();
        Device? still = await scope.ServiceProvider.GetRequiredService<SqliteDeviceStore>().GetAsync(id, CancellationToken.None);
        still!.ApprovalStatus.Should().Be(DeviceApprovalStatus.Pending);
    }

    [Fact]
    public async Task Approve_SucceedsForAdministrator()
    {
        long id = await SeedPendingAsync("router-b", "203.0.113.202");

        DeviceActionResult result = await AdminAs(Role.Administrator)
            .ApproveAsync(id, "Router B", "Juniper", "router", 15, [], CancellationToken.None);

        result.Ok.Should().BeTrue();
        using IServiceScope scope = _factory.Services.CreateScope();
        Device? approved = await scope.ServiceProvider.GetRequiredService<SqliteDeviceStore>().GetAsync(id, CancellationToken.None);
        approved!.ApprovalStatus.Should().Be(DeviceApprovalStatus.Approved);
        approved.Name.Should().Be("Router B");
    }

    [Fact]
    public async Task HostileDeviceHostname_RendersEncodedInTheDeviceLists()
    {
        const string payload = "<script>alert(document.cookie)</script>";
        await SeedPendingAsync(payload, "203.0.113.203");

        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        string pendingHtml = await admin.Client.GetStringAsync("/devices/pending");
        pendingHtml.Should().NotContain("<script>alert(document.cookie)</script>");
        pendingHtml.Should().Contain("alert(document.cookie)"); // present, but as encoded text
    }

}
