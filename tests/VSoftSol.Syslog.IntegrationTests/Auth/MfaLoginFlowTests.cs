using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Security.Mfa;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// v1.1 (B11-3) — TOTP MFA is now enforced at sign-in, not just enrollable. Mirrors
/// <c>AuthFlowTests</c>' static-SSR form-post pattern for the new second step.
/// </summary>
public sealed partial class MfaLoginFlowTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public MfaLoginFlowTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_WithoutMfaEnabled_StillSignsInDirectly()
    {
        // The overwhelming majority of accounts have no MFA enrolled — this must be a
        // completely unaffected regression.
        var auth = new WebAuthClient(_factory);
        await auth.SeedUserAsync("mfa-regress-none", "mfa-Test-Password-1", Role.Operator);

        HttpResponseMessage login = await auth.LoginAsync("mfa-regress-none", "mfa-Test-Password-1");

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_WithMfaEnabled_DoesNotSignInAfterPasswordAlone()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("mfa-step2-a", "mfa-Test-Password-1", Role.Operator);
        await auth.EnableMfaAsync(userId);

        HttpResponseMessage login = await auth.LoginAsync("mfa-step2-a", "mfa-Test-Password-1");

        login.StatusCode.Should().Be(HttpStatusCode.OK, "the response is the MFA code step, not a redirect to the app");
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should()
            .Be(HttpStatusCode.Redirect, "no session exists yet — the second factor has not been verified");
    }

    [Fact]
    public async Task Login_WithMfaEnabled_CorrectCode_SignsIn()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("mfa-step2-b", "mfa-Test-Password-1", Role.Operator);
        byte[] secret = await auth.EnableMfaAsync(userId);

        string page = await (await auth.LoginAsync("mfa-step2-b", "mfa-Test-Password-1")).Content.ReadAsStringAsync();
        string challengeToken = ChallengeTokenRegex().Match(page).Groups["v"].Value;
        string code = TotpGenerator.GenerateCode(secret, DateTimeOffset.UtcNow);

        HttpResponseMessage codeResponse = await PostMfaCodeAsync(auth, page, challengeToken, code);

        codeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_WithMfaEnabled_WrongCode_StaysUnauthenticated()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("mfa-step2-c", "mfa-Test-Password-1", Role.Operator);
        await auth.EnableMfaAsync(userId);

        string page = await (await auth.LoginAsync("mfa-step2-c", "mfa-Test-Password-1")).Content.ReadAsStringAsync();
        string challengeToken = ChallengeTokenRegex().Match(page).Groups["v"].Value;

        await PostMfaCodeAsync(auth, page, challengeToken, "000000");

        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Login_WithMfaEnabled_TooManyWrongCodes_DiscardsTheChallenge()
    {
        var auth = new WebAuthClient(_factory);
        long userId = await auth.SeedUserAsync("mfa-step2-d", "mfa-Test-Password-1", Role.Operator);
        byte[] secret = await auth.EnableMfaAsync(userId);

        string page = await (await auth.LoginAsync("mfa-step2-d", "mfa-Test-Password-1")).Content.ReadAsStringAsync();
        string challengeToken = ChallengeTokenRegex().Match(page).Groups["v"].Value;

        // Default WebAuthOptions.MfaMaxAttempts is 5 — exhaust it with wrong codes.
        HttpResponseMessage last = null!;
        for (int i = 0; i < 6; i++)
        {
            last = await PostMfaCodeAsync(auth, page, challengeToken, "000000");
        }

        // The now-discarded challenge must not work anymore even with the *correct* code.
        string validCode = TotpGenerator.GenerateCode(secret, DateTimeOffset.UtcNow);
        HttpResponseMessage afterDiscard = await PostMfaCodeAsync(auth, page, challengeToken, validCode);

        (await auth.Client.GetAsync("/dashboards")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        _ = last;
        _ = afterDiscard;
    }

    private static async Task<HttpResponseMessage> PostMfaCodeAsync(WebAuthClient auth, string page, string challengeToken, string code)
    {
        string token = AntiforgeryTokenRegex().Match(page).Groups["v"].Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login-mfa",
            ["__RequestVerificationToken"] = token,
            ["MfaModel.ChallengeToken"] = challengeToken,
            ["MfaModel.Code"] = code,
        });
        return await auth.Client.PostAsync("/login", form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("name=\"MfaModel\\.ChallengeToken\"[^>]*value=\"(?<v>[^\"]+)\"")]
    private static partial Regex ChallengeTokenRegex();
}
