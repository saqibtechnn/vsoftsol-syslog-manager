using System.Net;
using FluentAssertions;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 "Security headers — asserted by test, not by inspection" and the cookie-flag
/// checks (Secure, HttpOnly, SameSite=Strict).
/// </summary>
public sealed class SecurityHeadersTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public SecurityHeadersTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Csp_HasNoUnsafeInline_AndLocksDownFraming()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/login");

        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().NotContain("unsafe-inline");
        csp.Should().NotContain("unsafe-eval");
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("object-src 'none'");
        csp.Should().Contain("base-uri 'self'");
        csp.Should().Contain("form-action 'self'");
        csp.Should().MatchRegex("script-src 'self' 'nonce-[^']+'");
    }

    [Fact]
    public async Task StandardHeaders_ArePresent()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/login");

        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Single().Should().Be("same-origin");
        response.Headers.Contains("X-Powered-By").Should().BeFalse();
    }

    [Fact]
    public async Task EachResponse_GetsAFreshCspNonce()
    {
        HttpClient client = _factory.CreateClient();

        string one = (await client.GetAsync("/login")).Headers.GetValues("Content-Security-Policy").Single();
        string two = (await client.GetAsync("/login")).Headers.GetValues("Content-Security-Policy").Single();

        one.Should().NotBe(two, "the nonce must not be reused across responses");
    }

    [Fact]
    public async Task AuthCookie_IsSecure_HttpOnly_AndSameSiteStrict()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SeedUserAsync("cookie-user", "cookie-Test-Password-1", Core.Enums.Role.ReadOnly);

        HttpResponseMessage login = await auth.LoginAsync("cookie-user", "cookie-Test-Password-1");

        string setCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("vsoftsol.auth", StringComparison.Ordinal));
        setCookie.Should().Contain("secure", "the auth cookie must be Secure");
        setCookie.Should().Contain("httponly", "the auth cookie must be HttpOnly");
        setCookie.Should().Contain("samesite=strict");
    }

    [Fact]
    public async Task ProtectedPage_WithoutAuth_RedirectsRatherThan500()
    {
        HttpResponseMessage response = await _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync("/settings/users");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/login");
    }
}
