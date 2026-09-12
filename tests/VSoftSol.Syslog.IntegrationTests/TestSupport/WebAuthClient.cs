using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Security.Mfa;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Drives the real cookie-auth flow against <see cref="SyslogWebApplicationFactory"/>:
/// seed a user with a known password, submit the sign-in form (with its antiforgery
/// token), and keep the auth cookie on a single <see cref="HttpClient"/> for subsequent
/// requests.
/// </summary>
public sealed partial class WebAuthClient
{
    private readonly SyslogWebApplicationFactory _factory;

    public WebAuthClient(SyslogWebApplicationFactory factory)
    {
        _factory = factory;
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
    }

    /// <summary>Cookie-bearing client. After <see cref="LoginAsync"/> it carries the auth cookie.</summary>
    public HttpClient Client { get; }

    public async Task<long> SeedUserAsync(
        string username, string password, Role role,
        bool mustChangePassword = false, bool enabled = true,
        IReadOnlyList<long>? streamIds = null, IReadOnlyList<long>? deviceGroupIds = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<SqliteUserStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        long id = await users.CreateAsync(username, username, role, hasher.Hash(password), mustChangePassword,
            DateTimeOffset.UtcNow, CancellationToken.None);
        if (!enabled)
        {
            await users.UpdateProfileAsync(id, username, role, isEnabled: false, CancellationToken.None);
        }

        if (streamIds is not null || deviceGroupIds is not null)
        {
            await users.SetScopesAsync(id, streamIds ?? [], deviceGroupIds ?? [], CancellationToken.None);
        }

        return id;
    }

    /// <summary>
    /// v1.1 (B11-3): enables MFA on an already-seeded user with a known, fixed TOTP secret,
    /// so the test can compute a valid code deterministically via <see cref="TotpGenerator"/>
    /// instead of driving the real (random-secret) enrollment UI. The "mfa.totp." prefix
    /// must match <c>MfaSelfServiceService</c>'s own (private) secret-key convention.
    /// </summary>
    public async Task<byte[]> EnableMfaAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<SqliteSecretStore>();
        var users = scope.ServiceProvider.GetRequiredService<SqliteUserStore>();

        byte[] secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        await secrets.SetAsync("mfa.totp." + userId, Base32.Encode(secret), "test", CancellationToken.None);
        await users.SetMfaEnabledAsync(userId, true, DateTimeOffset.UtcNow, CancellationToken.None);
        return secret;
    }

    /// <summary>Submits the sign-in form. Returns the POST response (302 on success).</summary>
    public async Task<HttpResponseMessage> LoginAsync(string username, string password, string? returnUrl = null)
    {
        string loginUrl = returnUrl is null ? "/login" : $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";

        string page = await Client.GetStringAsync(loginUrl);
        string token = TokenRegex().Match(page).Groups["v"].Value;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Model.Username"] = username,
            ["Model.Password"] = password,
        });

        return await Client.PostAsync(loginUrl, form);
    }

    public async Task<HttpResponseMessage> SignInAsync(
        string username, string password, Role role, bool mustChangePassword = false,
        IReadOnlyList<long>? streamIds = null, IReadOnlyList<long>? deviceGroupIds = null)
    {
        await SeedUserAsync(username, password, role, mustChangePassword, enabled: true, streamIds, deviceGroupIds);
        return await LoginAsync(username, password);
    }

    /// <summary>The antiforgery token currently rendered on a form page.</summary>
    public async Task<string> ReadAntiforgeryTokenAsync(string path)
    {
        string page = await Client.GetStringAsync(path);
        return TokenRegex().Match(page).Groups["v"].Value;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"")]
    private static partial Regex TokenRegex();
}
