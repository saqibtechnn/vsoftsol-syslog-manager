using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests;

/// <summary>
/// PHASE_00 item 6: the Blazor Server host starts and serves the placeholder page.
/// Also proves the shared composition root wires without throwing and the Phase 0
/// baseline security headers are present.
/// </summary>
public sealed class WebHostSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebHostSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

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
}
