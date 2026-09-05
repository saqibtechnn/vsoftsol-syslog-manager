using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests;

/// <summary>
/// PHASE_00 item 6: the Blazor Server host starts and serves the placeholder page.
/// Also proves the shared composition root wires without throwing and the Phase 0
/// baseline security headers are present.
/// </summary>
public sealed class WebHostSmokeTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public WebHostSmokeTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_ReturnsPlaceholderPage()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<h1>");
    }

    [Fact]
    public async Task Root_SendsBaselineSecurityHeaders()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");

        response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? csp).Should().BeTrue();
        csp!.Single().Should().Contain("frame-ancestors 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
    }

    [Fact]
    public void Host_BuildsWithoutResolutionErrors()
    {
        // Do not dispose _factory here — it is shared across the class fixture.
        Action act = () => _ = _factory.Services;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Host_MigratesAndSeedsTheDatabaseOnStartup()
    {
        // Force the host (and its DatabaseInitializer hosted service) to start.
        using HttpClient client = _factory.CreateClient();
        _ = await client.GetAsync("/");

        var options = _factory.Services.GetRequiredService<IOptions<VSoftSol.Syslog.Data.Sqlite.SqliteDataOptions>>();
        File.Exists(options.Value.DatabasePath).Should().BeTrue();

        var repository = _factory.Services.GetRequiredService<VSoftSol.Syslog.Core.Abstractions.ILogRepository>();
        long id = await repository.AppendAsync(
            SampleEvents.Minimal("host wrote this"), CancellationToken.None);
        (await repository.GetByIdAsync(id, CancellationToken.None)).Should().NotBeNull();
    }
}
