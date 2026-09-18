using System.Net;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Security;

/// <summary>
/// Real-HTTP regression test for <c>/api/setup/first-message-status</c> (v1.1) — the
/// "Waiting for messages" page's client-side auto-advance poll. This 500'd on every single
/// poll: the handler resolved the acting user via <c>CurrentUserAccessor</c>, which wraps
/// Blazor Server's <c>ServerAuthenticationStateProvider</c> — that throws when called from a
/// plain minimal API endpoint handler, never a Razor component's circuit. Same root cause,
/// caught the same way, as the <c>/bundles/export</c> and <c>/reports/{id}/run</c> fixes
/// (`docs/evidence/v1.1-currentuseraccessor-endpoint-fix/`). Fixed by building
/// <c>CurrentUser</c> directly from <c>HttpContext.User</c>, matching the convention
/// <c>SearchEndpoints</c> already used correctly.
/// </summary>
[Trait("Category", "Setup")]
public sealed class SetupEndpointsWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public SetupEndpointsWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task FirstMessageStatus_AuthenticatedUser_Returns200_NotAServerError()
    {
        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        HttpResponseMessage response = await admin.Client.GetAsync("/api/setup/first-message-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("hasMessage");
    }

    [Fact]
    public async Task FirstMessageStatus_Unauthenticated_IsRefused_NotAServerError()
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync("/api/setup/first-message-status");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
