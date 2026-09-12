using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// The first-run wizard is unskippable (PHASE_12 build item 2, UX_STANDARDS.md §2): while
/// <see cref="FirstRunState"/> says setup has never completed, every request is sent to
/// <c>/setup</c> regardless of path or auth state — there is no working credential to sign
/// in with yet, so exempting <c>/login</c> would only show a login form that can never
/// succeed. Placed before <c>UseAuthentication</c>/<c>UseAuthorization</c> so it never races
/// the normal challenge-redirect-to-<c>/login</c> logic. Requests <see cref="UseStaticFiles"/>
/// already served (an existing file in <c>wwwroot</c>) never reach this middleware at all,
/// so no separate exemption list for CSS/JS/images is needed here.
/// </summary>
public sealed class FirstRunGateMiddleware
{
    private const string SetupPath = "/setup";

    private readonly RequestDelegate _next;

    public FirstRunGateMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, FirstRunState firstRun)
    {
        PathString path = context.Request.Path;

        // The Blazor Server circuit's own SignalR connection and framework assets are not
        // static files and must never be redirected mid-render.
        if (path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/_framework"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        bool isFirstRun = await firstRun.IsFirstRunAsync(context.RequestAborted).ConfigureAwait(false);
        bool onSetupPath = path.StartsWithSegments(SetupPath);

        if (isFirstRun && !onSetupPath)
        {
            context.Response.Redirect(SetupPath);
            return;
        }

        if (!isFirstRun && onSetupPath)
        {
            context.Response.Redirect("/dashboards");
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}

internal static class FirstRunGateMiddlewareExtensions
{
    public static IApplicationBuilder UseFirstRunGate(this IApplicationBuilder app) =>
        app.UseMiddleware<FirstRunGateMiddleware>();
}
