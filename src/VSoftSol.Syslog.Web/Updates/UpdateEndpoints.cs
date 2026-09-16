using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Updates;

/// <summary>
/// Hands the Administrator the already-downloaded, already-verified MSI to run themselves,
/// elevated, on their own machine (v1.1 — ADR 0021) — the Web process (an unprivileged
/// service account) only ever serves a file it already has; it never runs anything. Gated
/// independently of the Settings page's own <c>[Authorize]</c> attribute, per this
/// project's "role re-checked at the service" convention.
/// </summary>
public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/updates/download", DownloadAsync)
            .RequireAuthorization(AuthPolicies.Administer);

        return endpoints;
    }

    private static async Task DownloadAsync(HttpContext http, UpdateAdminService admin)
    {
        (Stream Content, string FileName)? download = await admin.GetVerifiedDownloadAsync(http.User, http.RequestAborted).ConfigureAwait(false);
        if (download is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using Stream content = download.Value.Content;
        http.Response.ContentType = "application/x-msi";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{download.Value.FileName}\"";
        await content.CopyToAsync(http.Response.Body, http.RequestAborted).ConfigureAwait(false);
    }
}
