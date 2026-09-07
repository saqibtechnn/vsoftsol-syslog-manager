using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Users;
using LocalAuthOptions = VSoftSol.Syslog.Data.Users.AuthenticationOptions;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>Outcome of a sign-in attempt, safe to surface to the login page (no enumeration).</summary>
public enum SignInStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    Disabled,
}

/// <summary>
/// Orchestrates password sign-in / sign-out / change against the authentication seam, the
/// server-side session store, and the audit log (PHASE_04 build items 2, 5, 6).
/// </summary>
public sealed class AuthSessionService
{
    private readonly IAuthenticationProvider _auth;
    private readonly SqliteUserStore _users;
    private readonly SqliteSessionStore _sessions;
    private readonly SqliteAuditLog _audit;
    private readonly IPasswordHasher _hasher;
    private readonly LocalAuthOptions _authOptions;
    private readonly WebAuthOptions _webOptions;
    private readonly TimeProvider _time;

    public AuthSessionService(
        IAuthenticationProvider auth,
        SqliteUserStore users,
        SqliteSessionStore sessions,
        SqliteAuditLog audit,
        IPasswordHasher hasher,
        IOptions<LocalAuthOptions> authOptions,
        IOptions<WebAuthOptions> webOptions,
        TimeProvider timeProvider)
    {
        _auth = auth;
        _users = users;
        _sessions = sessions;
        _audit = audit;
        _hasher = hasher;
        _authOptions = authOptions.Value;
        _webOptions = webOptions.Value;
        _time = timeProvider;
    }

    public async Task<SignInStatus> PasswordSignInAsync(
        HttpContext httpContext, string username, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        string? sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();

        AuthenticationResult result = await _auth.AuthenticateAsync(username ?? string.Empty, password ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.User is null)
        {
            SignInStatus status = result.FailureReason switch
            {
                AuthenticationFailureReason.AccountLocked => SignInStatus.LockedOut,
                AuthenticationFailureReason.AccountDisabled => SignInStatus.Disabled,
                _ => SignInStatus.InvalidCredentials,
            };

            await _audit.AppendAsync(
                new AuditEntry(
                    status == SignInStatus.LockedOut ? AuditActions.LoginLockout : AuditActions.LoginFailure,
                    Actor: username,
                    SourceIp: sourceIp,
                    Detail: result.FailureReason.ToString()),
                cancellationToken).ConfigureAwait(false);

            return status;
        }

        UserAccount? account = await _users.FindByUsernameAsync(result.User.Username, cancellationToken).ConfigureAwait(false);
        long userId = account?.UserId ?? 0;

        string sessionId = await _sessions.CreateAsync(
            userId,
            _time.GetUtcNow(),
            _webOptions.AbsoluteSessionLifetime,
            sourceIp,
            httpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken).ConfigureAwait(false);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            PrincipalFactory.Build(result.User, sessionId),
            new AuthenticationProperties
            {
                IsPersistent = false,
                IssuedUtc = _time.GetUtcNow(),
                ExpiresUtc = _time.GetUtcNow() + _webOptions.AbsoluteSessionLifetime,
            }).ConfigureAwait(false);

        await _audit.AppendAsync(
            new AuditEntry(AuditActions.LoginSuccess, Actor: result.User.Username, EntityType: "user",
                EntityId: userId.ToString(System.Globalization.CultureInfo.InvariantCulture), SourceIp: sourceIp),
            cancellationToken).ConfigureAwait(false);

        return SignInStatus.Success;
    }

    public async Task SignOutAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        string? sessionId = httpContext.User.FindFirst(SyslogClaimTypes.SessionId)?.Value;
        string? username = httpContext.User.Identity?.Name;
        if (!string.IsNullOrEmpty(sessionId))
        {
            await _sessions.RevokeAsync(sessionId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(username))
        {
            await _audit.AppendAsync(
                new AuditEntry(AuditActions.Logout, Actor: username, SourceIp: httpContext.Connection.RemoteIpAddress?.ToString()),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Changes the signed-in user's password (the forced-change flow and the normal one).
    /// Verifies the current password, enforces the minimum length, clears the must-change
    /// flag, and revokes all other sessions for the user.
    /// </summary>
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        HttpContext httpContext, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        string? username = httpContext.User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
        {
            return PasswordChangeResult.NotAuthenticated;
        }

        UserAccount? account = await _users.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (account?.PasswordHash is null ||
            !_hasher.Verify(currentPassword ?? string.Empty, account.PasswordHash, out _))
        {
            return PasswordChangeResult.CurrentPasswordWrong;
        }

        if ((newPassword ?? string.Empty).Length < _authOptions.MinimumPasswordLength)
        {
            return PasswordChangeResult.NewPasswordTooShort;
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return PasswordChangeResult.NewPasswordSameAsOld;
        }

        DateTimeOffset now = _time.GetUtcNow();
        await _users.SetPasswordAsync(account.UserId, _hasher.Hash(newPassword!), mustChangePassword: false, now, cancellationToken)
            .ConfigureAwait(false);

        string? currentSession = httpContext.User.FindFirst(SyslogClaimTypes.SessionId)?.Value;
        await _sessions.RevokeAllForUserAsync(account.UserId, now, cancellationToken).ConfigureAwait(false);

        // Keep the caller signed in on a fresh session.
        string newSession = await _sessions.CreateAsync(
            account.UserId, now, _webOptions.AbsoluteSessionLifetime,
            httpContext.Connection.RemoteIpAddress?.ToString(), httpContext.Request.Headers.UserAgent.ToString(), cancellationToken)
            .ConfigureAwait(false);
        _ = currentSession;

        AuthenticatedUser? refreshed = await _auth.RefreshAsync(username, cancellationToken).ConfigureAwait(false);
        if (refreshed is not null)
        {
            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                PrincipalFactory.Build(refreshed, newSession)).ConfigureAwait(false);
        }

        await _audit.AppendAsync(
            new AuditEntry(AuditActions.PasswordChange, Actor: username, EntityType: "user",
                EntityId: account.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceIp: httpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        return PasswordChangeResult.Success;
    }
}

public enum PasswordChangeResult
{
    Success,
    NotAuthenticated,
    CurrentPasswordWrong,
    NewPasswordTooShort,
    NewPasswordSameAsOld,
}
