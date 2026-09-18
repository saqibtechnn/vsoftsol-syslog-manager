using System.Globalization;
using System.Security.Claims;
using System.Text;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.Reporting.Csv;
using VSoftSol.Syslog.Reporting.Pdf;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Reports;

/// <summary>An on-demand "Run now" result — PDF and CSV bytes ready for download.</summary>
public sealed record ReportRenderResult(byte[] Pdf, byte[] Csv, int RowCount, string FileNameStem);

/// <summary>
/// Runs a report on demand under the <em>current viewer's</em> scope (PHASE_10 build item 7)
/// — "Run now" always reflects exactly what the person clicking the button can see, whether
/// or not they own the report. Every run is audited, matching the export audit precedent
/// from Phase 5.
/// </summary>
public sealed class ReportRenderService
{
    private readonly ReportContentReader _content;
    private readonly SqliteAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly IWebHostEnvironmentLogoResolver _logo;

    public ReportRenderService(
        ReportContentReader content, SqliteAuditLog audit,
        TimeProvider? timeProvider, IWebHostEnvironmentLogoResolver logo)
    {
        _content = content;
        _audit = audit;
        _time = timeProvider ?? TimeProvider.System;
        _logo = logo;
    }

    /// <summary>
    /// Takes the caller's <see cref="ClaimsPrincipal"/> directly rather than
    /// <see cref="CurrentUserAccessor"/>: this is called exclusively from the plain minimal
    /// API endpoint <c>ReportEndpoints.RunAsync</c> (the Razor "Run now" button is a full page
    /// navigation to that route, never a circuit-scoped call) — <c>CurrentUserAccessor</c>
    /// wraps Blazor Server's own <c>ServerAuthenticationStateProvider</c>, which throws
    /// <c>InvalidOperationException</c> when resolved outside a Razor component's circuit.
    /// Confirmed as a live 500 on every "Run now" report download (v1.1, same root cause as
    /// ADR 0021's `UpdateAdminService.GetVerifiedDownloadAsync` fix, and
    /// `ConfigBundleAdminService.ExportAsync`'s) — here it also determined the *scope* the
    /// report content resolved under, not just the audit actor, so this was a correctness bug
    /// as well as an availability one.
    /// </summary>
    public async Task<ReportRenderResult> RunNowAsync(ReportDefinition report, ClaimsPrincipal caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var user = new CurrentUser(caller);

        ReportContent content = await _content
            .ResolveAsync(report, user.Scope, user.DisplayName, _time.GetUtcNow(), ct).ConfigureAwait(false);

        byte[] logo = await _logo.TryReadLogoWideAsync(ct).ConfigureAwait(false) ?? [];
        byte[] pdf = ReportPdfRenderer.Render(content, logo.Length > 0 ? logo : null);

        await using var writer = new StringWriter();
        await ReportCsvWriter.WriteAsync(writer, content, ct).ConfigureAwait(false);
        byte[] csv = Encoding.UTF8.GetBytes(writer.ToString());

        string detail = content.Error is { } error
            ? $"{report.Name}: failed to run — {error}"
            : $"{report.Name}: {content.RowCount} row(s), run on demand";
        await _audit.AppendAsync(
            new AuditEntry(AuditActions.ReportRun, user.UserName, "report", report.Id.ToString(CultureInfo.InvariantCulture), Detail: detail),
            CancellationToken.None).ConfigureAwait(false);

        string stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string stem = string.Join('_', report.Name.Split(Path.GetInvalidFileNameChars())) + "-" + stamp;
        return new ReportRenderResult(pdf, csv, content.RowCount, stem);
    }
}

/// <summary>Resolves the branding wide logo for embedding in a rendered report — a small
/// seam so <see cref="ReportRenderService"/> stays unit-testable without real wwwroot files.</summary>
public interface IWebHostEnvironmentLogoResolver
{
    Task<byte[]?> TryReadLogoWideAsync(CancellationToken cancellationToken);
}
