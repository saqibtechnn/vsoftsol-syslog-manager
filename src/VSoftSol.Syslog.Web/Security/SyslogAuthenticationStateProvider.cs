using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Keeps a live Blazor circuit's authentication state fresh: every
/// <see cref="RevalidationInterval"/> it re-checks the server-side session and the user
/// record, so an admin revoking the session or changing the user's role/scope takes effect
/// on the open tab without a reload (PHASE_04 build item 5).
/// </summary>
public sealed class SyslogAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WebAuthOptions _options;
    private readonly TimeProvider _time;

    public SyslogAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<WebAuthOptions> options,
        TimeProvider timeProvider)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _time = timeProvider;
    }

    protected override TimeSpan RevalidationInterval => _options.RevalidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ClaimsPrincipal principal = authenticationState.User;
        string? sessionId = principal.FindFirst(SyslogClaimTypes.SessionId)?.Value;
        string? username = principal.Identity?.Name;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(username))
        {
            return false;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SqliteSessionStore>();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationProvider>();

        UserSession? session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !session.IsActiveAt(_time.GetUtcNow(), _options.IdleTimeout))
        {
            return false;
        }

        AuthenticatedUser? user = await auth.RefreshAsync(username, cancellationToken).ConfigureAwait(false);
        return user is not null && string.Equals(user.Role.ToString(), principal.FindFirst(ClaimTypes.Role)?.Value, StringComparison.Ordinal);
    }
}
