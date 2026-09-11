using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Reports;

/// <summary>The "Run now" download route (PHASE_10 build item 7) — a GET so the result is a
/// shareable link, mirroring the Phase 5 search-export route.</summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/reports/{id:long}/run", RunAsync)
            .RequireAuthorization(AuthPolicies.ViewReports);

        return endpoints;
    }

    private static async Task RunAsync(HttpContext http, long id, ReportAdminService admin, ReportRenderService renderer)
    {
        ReportDefinition? report = await admin.GetAsync(id, http.RequestAborted).ConfigureAwait(false);
        if (report is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ReportRenderResult result = await renderer.RunNowAsync(report, http.RequestAborted).ConfigureAwait(false);

        bool csv = string.Equals(http.Request.Query["format"], "csv", StringComparison.OrdinalIgnoreCase);
        byte[] body = csv ? result.Csv : result.Pdf;
        string extension = csv ? "csv" : "pdf";
        http.Response.ContentType = csv ? "text/csv; charset=utf-8" : "application/pdf";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{result.FileNameStem}.{extension}\"";
        await http.Response.Body.WriteAsync(body, http.RequestAborted).ConfigureAwait(false);
    }
}
