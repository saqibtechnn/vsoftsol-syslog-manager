using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Security;

/// <summary>
/// PHASE_12 build item 2 — end to end: a database that has never had a password set on any
/// account is redirected everywhere to <c>/setup</c> (the wizard is unskippable), and a
/// database with at least one real credential (every other integration test's baseline
/// state) behaves exactly as it always has. Each test gets its own factory/database rather
/// than an <c>IClassFixture</c> — <see cref="VSoftSol.Syslog.Web.Security.FirstRunState"/>
/// is a singleton that latches "setup complete" for the life of the host, so sharing one
/// database across these tests would make the fresh-database assertions order-dependent on
/// whichever test happened to seed a user first.
/// </summary>
public sealed class FirstRunGateWebTests
{
    [Fact]
    public async Task Get_AnyPage_OnAFreshDatabase_RedirectsToSetup()
    {
        await using var factory = new SyslogWebApplicationFactory { SeedCompletedSetup = false };
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        HttpResponseMessage response = await client.GetAsync("/dashboards");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/setup");
    }

    [Fact]
    public async Task Get_Setup_OnAFreshDatabase_IsReachableAnonymously()
    {
        await using var factory = new SyslogWebApplicationFactory { SeedCompletedSetup = false };
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        HttpResponseMessage response = await client.GetAsync("/setup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_Login_OnceASetupHasCompleted_IsNoLongerRedirectedToSetup()
    {
        await using var factory = new SyslogWebApplicationFactory();

        // Every other integration test's baseline: seed a real user with a real password,
        // exactly like WebAuthClient.SeedUserAsync always does, then confirm ordinary
        // navigation is completely unaffected by the gate.
        var auth = new WebAuthClient(factory);
        await auth.SeedUserAsync("gate-regression", "gate-Test-Password-1", Role.Operator);

        HttpResponseMessage login = await auth.Client.GetAsync("/login");

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
