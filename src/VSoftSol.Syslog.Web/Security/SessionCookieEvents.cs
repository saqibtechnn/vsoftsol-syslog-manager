using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Validates the auth cookie against the server-side session on every request: a revoked
/// (logged-out or admin-revoked) session, an idle- or absolute-expired session, or a
/// now-disabled/deleted user all reject the principal immediately. On success the
/// principal is rebuilt from the current user record so role and scope changes take effect
/// without a re-login (PHASE_04 build item 5; SECURITY_STANDARDS.md "Session attacks").
/// </summary>
public sealed class SessionCookieEvents : CookieAuthenticationEvents
{
    private readonly SqliteSessionStore _sessions;
    private readonly IAuthenticationProvider _auth;
    private readonly WebAuthOptions _options;
    private readonly TimeProvider _time;

    public SessionCookieEvents(
        SqliteSessionStore sessions,
        IAuthenticationProvider auth,
        IOptions<WebAuthOptions> options,
        TimeProvider timeProvider)
    {
        _sessions = sessions;
        _auth = auth;
        _options = options.Value;
        _time = timeProvider;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        string? sessionId = context.Principal?.FindFirst(SyslogClaimTypes.SessionId)?.Value;
        string? username = context.Principal?.Identity?.Name;
        CancellationToken ct = context.HttpContext.RequestAborted;

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(username))
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        UserSession? session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        if (session is null || !session.IsActiveAt(now, _options.IdleTimeout))
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        AuthenticatedUser? user = await _auth.RefreshAsync(username, ct).ConfigureAwait(false);
        if (user is null)
        {
            // user disabled or deleted since sign-in
            await _sessions.RevokeAsync(sessionId, now, ct).ConfigureAwait(false);
            await Reject(context).ConfigureAwait(false);
            return;
        }

        context.ReplacePrincipal(PrincipalFactory.Build(user, sessionId));
        context.ShouldRenew = true;
        await _sessions.TouchAsync(sessionId, now, ct).ConfigureAwait(false);
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
