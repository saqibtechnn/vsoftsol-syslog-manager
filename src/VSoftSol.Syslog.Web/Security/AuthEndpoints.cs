using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// The one non-component auth route: sign-out. Sign-in and password-change are SSR form
/// components so they can be built entirely from the design system (PHASE_04 UX gate).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/logout", async (HttpContext http, AuthSessionService auth) =>
        {
            await auth.SignOutAsync(http, http.RequestAborted).ConfigureAwait(false);
            return Results.Redirect("/login");
        });

        return endpoints;
    }
}
