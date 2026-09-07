using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Reporting.Export;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Search;

/// <summary>
/// The search export route (PHASE_05 item 7). A GET so a result set is a shareable link;
/// streamed, size-capped, and written to the audit log. Not a component — it produces a
/// file download, not a page.
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/search/export", ExportAsync)
            .RequireAuthorization(AuthPolicies.ViewData);

        return endpoints;
    }

    private static async Task ExportAsync(
        HttpContext http,
        ScopedEventReader reader,
        SqliteAuditLog audit,
        IOptions<SearchOptions> options)
    {
        IQueryCollection q = http.Request.Query;
        string queryText = q["q"].ToString();

        if (!TryParseInstant(q["from"], out DateTimeOffset from) || !TryParseInstant(q["to"], out DateTimeOffset to) || to <= from)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "A valid 'from' and 'to' time range is required." }).ConfigureAwait(false);
            return;
        }

        SearchParseResult parsed = SearchQueryParser.Parse(queryText);
        if (!parsed.Success)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = parsed.Error, position = parsed.ErrorPosition }).ConfigureAwait(false);
            return;
        }

        ExportFormat format = (q["format"].ToString().ToLowerInvariant()) switch
        {
            "json" => ExportFormat.Json,
            "raw" or "text" or "log" => ExportFormat.RawText,
            _ => ExportFormat.Csv,
        };

        var user = new CurrentUser(http.User);
        var request = new SearchRequest
        {
            QueryText = queryText,
            FromUtc = from,
            ToUtc = to,
            Limit = options.Value.ExportMaxRows,
            Sort = ParseSort(q["sort"], q["dir"]),
        };

        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string fileName = $"vsoftsol-search-{stamp}.{SearchExportWriter.FileExtension(format)}";
        http.Response.ContentType = SearchExportWriter.ContentType(format);
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        int rows;
        await using (var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            rows = await SearchExportWriter.WriteAsync(
                format, writer, reader.SearchStreamAsync(user.Scope, request, http.RequestAborted), http.RequestAborted)
                .ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        await audit.AppendAsync(
            new AuditEntry(
                AuditActions.Export,
                Actor: user.UserName,
                EntityType: "search",
                SourceIp: http.Connection.RemoteIpAddress?.ToString(),
                Detail: $"format={format}; rows={rows}; from={from:o}; to={to:o}; query={queryText}"),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryParseInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);

    private static SearchSort ParseSort(string? field, string? dir)
    {
        SearchSortField parsed = field?.ToLowerInvariant() switch
        {
            "severity" => SearchSortField.Severity,
            "facility" => SearchSortField.Facility,
            "host" => SearchSortField.Host,
            "source_ip" or "ip" => SearchSortField.SourceIp,
            "app" => SearchSortField.App,
            "vendor" => SearchSortField.Vendor,
            "event_time" => SearchSortField.EventUtc,
            _ => SearchSortField.ReceivedUtc,
        };

        bool descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        return new SearchSort(parsed, descending);
    }
}
