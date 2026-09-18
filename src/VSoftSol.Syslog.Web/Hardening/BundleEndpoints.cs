using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Hardening;

/// <summary>The config bundle download route (PHASE_11 item 4) — a GET so the export is a
/// shareable link, the same convention as the Phase 10 report "run now" download.</summary>
public static class BundleEndpoints
{
    public static IEndpointRouteBuilder MapBundleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/bundles/export", ExportAsync).RequireAuthorization(AuthPolicies.Administer);

        return endpoints;
    }

    private static async Task ExportAsync(HttpContext http, ConfigBundleAdminService admin)
    {
        string sectionsParam = http.Request.Query["sections"].ToString();
        IReadOnlySet<string> sections = sectionsParam.Length == 0
            ? new HashSet<string>(BundleSections.All)
            : new HashSet<string>(sectionsParam.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        string title = string.IsNullOrWhiteSpace(http.Request.Query["title"]) ? "Export" : http.Request.Query["title"].ToString();

        SignedBundle bundle = await admin.ExportAsync(sections, title, http.User, http.RequestAborted).ConfigureAwait(false);
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle));
        string fileName = $"{Sanitize(title)}.vsbundle.json";

        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        await http.Response.Body.WriteAsync(body, http.RequestAborted).ConfigureAwait(false);
    }

    private static string Sanitize(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
