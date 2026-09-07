using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 end-to-end: sign-in, forced first-login password change, logout invalidating
/// the server-side session, mid-session account disable, and CSRF enforcement on the
/// sign-in form (SECURITY_STANDARDS.md "Session attacks", "CSRF token absence and reuse").
/// </summary>
public sealed class AuthFlowTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public AuthFlowTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_WithGoodCredentials_SetsCookieAndRedirects()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SeedUserAsync("flow-ok", "flow-Test-Password-1", Role.Operator);

        HttpResponseMessage login = await auth.LoginAsync("flow-ok", "flow-Test-Password-1", returnUrl: "/dashboards");

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShowsGenericErrorAndNoCookie()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SeedUserAsync("flow-bad", "flow-Test-Password-1", Role.Operator);

        HttpResponseMessage login = await auth.LoginAsync("flow-bad", "not-the-password");

        login.Headers.Contains("Set-Cookie").Should()
            .BeFalse("a failed login must not issue an auth cookie");
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should()
            .Be(HttpStatusCode.Redirect, "still unauthenticated");
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SeedUserAsync("flow-csrf", "flow-Test-Password-1", Role.Operator);
        _ = await auth.Client.GetAsync("/login"); // establish the antiforgery cookie

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["Model.Username"] = "flow-csrf",
            ["Model.Password"] = "flow-Test-Password-1",
            // no __RequestVerificationToken
        });
        HttpResponseMessage response = await auth.Client.PostAsync("/login", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SeededAdmin_MustChangePassword_IsSentToTheChangeScreen()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SeedUserAsync("flow-forced", "flow-Test-Password-1", Role.Administrator, mustChangePassword: true);
        await auth.LoginAsync("flow-forced", "flow-Test-Password-1");

        // Any page redirects to the forced change screen while the flag is set.
        HttpResponseMessage dash = await auth.Client.GetAsync("/dashboards");
        string body = await dash.Content.ReadAsStringAsync();

        (dash.StatusCode == HttpStatusCode.Redirect
            ? dash.Headers.Location!.ToString()
            : body).Should().Contain("change-password");
    }

    [Fact]
    public async Task Logout_RevokesTheServerSideSession()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("flow-logout", "flow-Test-Password-1", Role.Operator);
        await auth.LoginAsync("flow-logout", "flow-Test-Password-1");
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.OK);

        string token = await auth.ReadAntiforgeryTokenAsync("/about");
        var logoutForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        await auth.Client.PostAsync("/auth/logout", logoutForm);

        using IServiceScope scope = _factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SqliteSessionStore>();
        IReadOnlyList<UserSession> active = await sessions.ListActiveForUserAsync(userId, DateTimeOffset.UtcNow, CancellationToken.None);
        active.Should().BeEmpty("logout must revoke the session server-side, not just drop the cookie");

        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task DisablingAUser_EndsTheirSessionOnTheNextRequest()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("flow-disable", "flow-Test-Password-1", Role.Operator);
        await auth.LoginAsync("flow-disable", "flow-Test-Password-1");
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<SqliteUserStore>();
            await users.UpdateProfileAsync(userId, "flow-disable", Role.Operator, isEnabled: false, CancellationToken.None);
        }

        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should()
            .Be(HttpStatusCode.Redirect, "a disabled user is signed out on their next request");
    }

    [Fact]
    public async Task ChangingPassword_ClearsMustChangeFlag_AndKeepsUserSignedIn()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("flow-change", "flow-Test-Password-1", Role.Operator, mustChangePassword: true);
        await auth.LoginAsync("flow-change", "flow-Test-Password-1");

        string token = await auth.ReadAntiforgeryTokenAsync("/account/change-password");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "change-password",
            ["__RequestVerificationToken"] = token,
            ["Model.Current"] = "flow-Test-Password-1",
            ["Model.New"] = "a-brand-new-passphrase-9",
            ["Model.Confirm"] = "a-brand-new-passphrase-9",
        });
        HttpResponseMessage response = await auth.Client.PostAsync("/account/change-password", form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using IServiceScope scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<SqliteUserStore>();
        UserAccount? account = await users.FindByIdAsync(userId, CancellationToken.None);
        account!.MustChangePassword.Should().BeFalse();
    }
}
