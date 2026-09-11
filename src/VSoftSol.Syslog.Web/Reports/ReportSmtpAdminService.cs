using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Reports;

/// <summary>Global SMTP profile for scheduled report delivery, including the "test SMTP"
/// button UX_STANDARDS.md §4 requires for every configuration screen with real-world
/// consequences. Administrator-only, matching the retention settings page.</summary>
public sealed class ReportSmtpAdminService
{
    private readonly SqliteReportSmtpSettingsStore _store;
    private readonly ReportEmailSender _sender;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;

    public ReportSmtpAdminService(SqliteReportSmtpSettingsStore store, ReportEmailSender sender, SqliteAuditLog audit, CurrentUserAccessor users)
    {
        _store = store;
        _sender = sender;
        _audit = audit;
        _users = users;
    }

    public Task<ReportSmtpSettings> GetAsync(CancellationToken ct) => _store.GetAsync(ct);

    public async Task<(bool Ok, string Message)> SaveAsync(ReportSmtpSettings settings, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        await _store.SaveAsync(settings, user.UserName, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.ConfigChange, user.UserName, "report_smtp", null, Detail: "SMTP delivery settings changed"), CancellationToken.None)
            .ConfigureAwait(false);
        return (true, "SMTP settings saved.");
    }

    /// <summary>Sends a real, harmless test message to the given address. Never has a side
    /// effect beyond the one email (UX_STANDARDS.md §4 — "Test never has a side effect").</summary>
    public async Task<(bool Ok, string Message)> SendTestAsync(string recipient, CancellationToken ct)
    {
        (bool ok, string? error) = await _sender.SendAsync(
            [recipient],
            $"{BrandingInfo.ProductName} — test message",
            "This is a test of the report-delivery SMTP settings. If you received this, delivery is working.",
            [],
            ct).ConfigureAwait(false);

        return ok ? (true, $"Test message sent to {recipient}.") : (false, error ?? "The test message could not be sent.");
    }
}
