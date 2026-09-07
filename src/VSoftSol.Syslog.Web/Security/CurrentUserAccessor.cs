using Microsoft.AspNetCore.Components.Authorization;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Resolves the current <see cref="CurrentUser"/> from the Blazor
/// <see cref="AuthenticationStateProvider"/>, which is populated both during static SSR
/// (from <c>HttpContext.User</c>) and on an interactive circuit. Services that need the
/// caller's <c>UserScope</c> (e.g. before calling <c>ScopedEventReader</c>) inject this.
/// </summary>
public sealed class CurrentUserAccessor
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public CurrentUserAccessor(AuthenticationStateProvider authenticationStateProvider) =>
        _authenticationStateProvider = authenticationStateProvider;

    public async ValueTask<CurrentUser> GetAsync()
    {
        AuthenticationState state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return new CurrentUser(state.User);
    }
}
