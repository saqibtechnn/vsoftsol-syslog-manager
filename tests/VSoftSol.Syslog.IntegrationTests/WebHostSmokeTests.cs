using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests;

/// <summary>
/// The Blazor host starts, shares the composition root, and (from Phase 4) requires
/// authentication for every page except the sign-in screen. Updated from the Phase 0
/// placeholder assertions when Phase 4 added the fallback authorization policy — recorded
/// in PROGRESS.md.
/// </summary>
public sealed class WebHostSmokeTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public WebHostSmokeTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task UnauthenticatedRequest_ToAProtectedPage_RedirectsToLogin()
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.GetAsync("/dashboards");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/login").And.Contain("returnUrl");
    }

    [Fact]
    public async Task Login_ReturnsTheSignInForm()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sign in").And.Contain("name=\"Model.Username\"");
    }

    [Fact]
    public async Task Login_SendsHardenedSecurityHeaders()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/login");

        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().NotContain("unsafe-inline");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
    }

    [Fact]
    public void Host_BuildsWithoutResolutionErrors()
    {
        Action act = () => _ = _factory.Services;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Host_MigratesAndSeedsTheDatabaseOnStartup()
    {
        using HttpClient client = _factory.CreateClient();
        _ = await client.GetAsync("/login");

        var options = _factory.Services.GetRequiredService<IOptions<VSoftSol.Syslog.Data.Sqlite.SqliteDataOptions>>();
        File.Exists(options.Value.DatabasePath).Should().BeTrue();

        var repository = _factory.Services.GetRequiredService<VSoftSol.Syslog.Core.Abstractions.ILogRepository>();
        long id = await repository.AppendAsync(SampleEvents.Minimal("host wrote this"), CancellationToken.None);
        (await repository.GetByIdAsync(id, CancellationToken.None)).Should().NotBeNull();
    }
}
