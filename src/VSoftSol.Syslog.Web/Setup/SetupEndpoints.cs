using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Setup;

/// <summary>
/// PHASE_12 build item 2a — polled by the "Waiting for messages" page's client-side script
/// (no inline script; see wwwroot/js/app.js) to auto-advance to the dashboard the instant
/// the collector has stored its first event.
/// </summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/first-message-status",
            async (HttpContext http, ScopedEventReader reader, CancellationToken ct) =>
        {
            // A plain minimal API endpoint, not a Razor component circuit — CurrentUserAccessor
            // wraps Blazor Server's ServerAuthenticationStateProvider, which throws when
            // resolved outside a circuit (v1.1, same root cause as ADR 0021's
            // UpdateAdminService.GetVerifiedDownloadAsync fix). Build the CurrentUser directly
            // from HttpContext.User instead, matching SearchEndpoints' existing convention.
            var user = new CurrentUser(http.User);
            long count = await reader.CountAsync(user.Scope, new LogQuery { Limit = 1 }, ct).ConfigureAwait(false);
            return Results.Ok(new { hasMessage = count > 0 });
        }).RequireAuthorization();

        return endpoints;
    }
}
