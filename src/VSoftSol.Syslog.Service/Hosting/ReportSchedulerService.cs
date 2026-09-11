using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Reporting.Csv;
using VSoftSol.Syslog.Reporting.Pdf;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The scheduled-report engine (PHASE_10 build item 8). Runs off the ingest path in the
/// collector host only. Each tick resolves every due report under its <em>owner's</em>
/// <see cref="UserScope"/> — a scheduled report reflects exactly what its owner could see if
/// they ran it by hand, never more — renders PDF and CSV, delivers them, and reschedules.
/// </summary>
public sealed class ReportSchedulerService : BackgroundService
{
    private readonly SqliteReportStore _reports;
    private readonly ReportContentReader _content;
    private readonly SqliteUserStore _users;
    private readonly ReportEmailSender _email;
    private readonly SqliteAuditLog _audit;
    private readonly NotificationSink _notifications;
    private readonly SqliteConnectionFactory _factory;
    private readonly ReportSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReportSchedulerService> _logger;

    public ReportSchedulerService(
        SqliteReportStore reports,
        ReportContentReader content,
        SqliteUserStore users,
        ReportEmailSender email,
        SqliteAuditLog audit,
        NotificationSink notifications,
        SqliteConnectionFactory factory,
        IOptions<ReportSchedulerOptions> options,
        TimeProvider time,
        ILogger<ReportSchedulerService> logger)
    {
        _reports = reports;
        _content = content;
        _users = users;
        _email = email;
        _audit = audit;
        _notifications = notifications;
        _factory = factory;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Report scheduler started (tick {Tick}).", _options.TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report scheduler tick failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_options.TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Report scheduler stopped.");
    }

    /// <summary>One scheduler pass. Public so tests can drive it deterministically.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        IReadOnlyList<ReportDefinition> due = await _reports.ListDueAsync(now, cancellationToken).ConfigureAwait(false);

        foreach (ReportDefinition report in due)
        {
            await RunOneAsync(report, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunOneAsync(ReportDefinition report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        long runId = await _reports.StartRunAsync(report.Id, "scheduler", cancellationToken).ConfigureAwait(false);

        UserAccount? owner = report.OwnerUserId is { } ownerId
            ? await _users.FindByIdAsync(ownerId, cancellationToken).ConfigureAwait(false)
            : null;
        UserScope scope = owner is null ? UserScope.Unrestricted : UserScope.Create(owner.VisibleStreamIds, owner.VisibleDeviceGroupIds);
        string generatingUser = owner?.DisplayName ?? "system";

        ReportContent content;
        try
        {
            content = await _content.ResolveAsync(report, scope, generatingUser, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Report '{Name}' (#{Id}) failed to resolve.", report.Name, report.Id);
            await _reports.CompleteRunAsync(runId, ReportRunStatus.Failed, null, null, null, ex.Message, cancellationToken).ConfigureAwait(false);
            await AdvanceAndAuditFailureAsync(report, now, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[]? logo = await TryReadLogoAsync(cancellationToken).ConfigureAwait(false);
        byte[] pdfBytes = ReportPdfRenderer.Render(content, logo);
        string csvText;
        await using (var writer = new StringWriter())
        {
            await ReportCsvWriter.WriteAsync(writer, content, cancellationToken).ConfigureAwait(false);
            csvText = writer.ToString();
        }

        (string pdfPath, string csvPath) = await WriteOutputFilesAsync(report, now, pdfBytes, csvText, cancellationToken).ConfigureAwait(false);

        await _reports.CompleteRunAsync(runId, ReportRunStatus.Ok, content.RowCount, pdfPath, csvPath, null, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            new AuditEntry(AuditActions.ReportRun, Actor: "scheduler", EntityType: "report", EntityId: report.Id.ToString(CultureInfo.InvariantCulture),
                Detail: $"{report.Name}: {content.RowCount} row(s)"),
            CancellationToken.None).ConfigureAwait(false);

        await DeliverAsync(report, runId, pdfBytes, csvText, cancellationToken).ConfigureAwait(false);

        DateTimeOffset? next = report.Schedule.NextRunAfter(now);
        await _reports.AdvanceNextRunAsync(report.Id, next, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync(ReportDefinition report, long runId, byte[] pdfBytes, string csvText, CancellationToken cancellationToken)
    {
        if (report.Delivery.Type == ReportDeliveryType.None)
        {
            return;
        }

        (bool ok, string? error) = (false, "no delivery attempted");
        for (int attempt = 1; attempt <= Math.Max(1, _options.MaxDeliveryAttempts); attempt++)
        {
            (ok, error) = report.Delivery.Type == ReportDeliveryType.Email
                ? await _email.SendAsync(
                    report.Delivery.Recipients,
                    $"{report.Name} — generated {DateTime.UtcNow:yyyy-MM-dd}",
                    $"The scheduled report '{report.Name}' is attached as PDF and CSV.",
                    [("report.pdf", pdfBytes, "application/pdf"), ("report.csv", System.Text.Encoding.UTF8.GetBytes(csvText), "text/csv")],
                    cancellationToken).ConfigureAwait(false)
                : await TryWriteToFolderAsync(report.Delivery.FolderPath, report.Name, pdfBytes, csvText, cancellationToken).ConfigureAwait(false);

            if (ok)
            {
                break;
            }

            if (attempt < _options.MaxDeliveryAttempts)
            {
                await Task.Delay(_options.DeliveryRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        await _reports.RecordDeliveryAsync(runId, ok, error, cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            await _audit.AppendAsync(
                new AuditEntry(AuditActions.ReportDelivered, Actor: "scheduler", EntityType: "report", EntityId: report.Id.ToString(CultureInfo.InvariantCulture)),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _audit.AppendAsync(
                new AuditEntry(AuditActions.ReportDeliveryFailed, Actor: "scheduler", EntityType: "report",
                    EntityId: report.Id.ToString(CultureInfo.InvariantCulture), Detail: error),
                CancellationToken.None).ConfigureAwait(false);
            await _notifications(
                NotificationLevel.Warning, $"Report delivery failed: {report.Name}", error ?? "unknown error", null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<(bool Ok, string? Error)> TryWriteToFolderAsync(
        string? folderPath, string reportName, byte[] pdfBytes, string csvText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return (false, "no folder path configured");
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            string safeName = string.Join('_', reportName.Split(Path.GetInvalidFileNameChars()));
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            await File.WriteAllBytesAsync(Path.Combine(folderPath, $"{safeName}-{stamp}.pdf"), pdfBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folderPath, $"{safeName}-{stamp}.csv"), csvText, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(string PdfPath, string CsvPath)> WriteOutputFilesAsync(
        ReportDefinition report, DateTimeOffset now, byte[] pdfBytes, string csvText, CancellationToken cancellationToken)
    {
        string root = string.IsNullOrWhiteSpace(_options.OutputDirectory)
            ? Path.Combine(Path.GetDirectoryName(_factory.DatabasePath) ?? ".", "reports")
            : _options.OutputDirectory;
        Directory.CreateDirectory(root);

        string safeName = string.Join('_', report.Name.Split(Path.GetInvalidFileNameChars()));
        string stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string pdfPath = Path.Combine(root, $"{safeName}-{stamp}.pdf");
        string csvPath = Path.Combine(root, $"{safeName}-{stamp}.csv");

        await File.WriteAllBytesAsync(pdfPath, pdfBytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(csvPath, csvText, cancellationToken).ConfigureAwait(false);

        return (pdfPath, csvPath);
    }

    private async Task<byte[]?> TryReadLogoAsync(CancellationToken cancellationToken)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "branding", "logo-wide.png");
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task AdvanceAndAuditFailureAsync(ReportDefinition report, DateTimeOffset now, string error, CancellationToken cancellationToken)
    {
        await _audit.AppendAsync(
            new AuditEntry(AuditActions.ReportDeliveryFailed, Actor: "scheduler", EntityType: "report",
                EntityId: report.Id.ToString(CultureInfo.InvariantCulture), Detail: $"generation failed: {error}"),
            CancellationToken.None).ConfigureAwait(false);
        await _notifications(NotificationLevel.Warning, $"Report generation failed: {report.Name}", error, null, cancellationToken).ConfigureAwait(false);
        await _reports.AdvanceNextRunAsync(report.Id, report.Schedule.NextRunAfter(now), cancellationToken).ConfigureAwait(false);
    }
}
