using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The retention scheduler (PHASE_10 build items 2/4/5; ADR 0018). Runs off the ingest path
/// in the collector host only. Each tick runs one bounded batch of every stage — Hot→Warm,
/// Warm→Cold export, restore expiry, archive re-verification, and Delete-horizon purge —
/// so no stage can starve the others and the writer lock is never held longer than one
/// batch's transaction (CLAUDE.md Constraint 3).
/// </summary>
public sealed class RetentionTieringService : BackgroundService
{
    private readonly SqliteRetentionEngine _engine;
    private readonly SqliteArchiveVerifier _verifier;
    private readonly SqliteAuditLog _audit;
    private readonly NotificationSink _notifications;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionTieringService> _logger;

    public RetentionTieringService(
        SqliteRetentionEngine engine,
        SqliteArchiveVerifier verifier,
        SqliteAuditLog audit,
        NotificationSink notifications,
        IOptions<RetentionOptions> options,
        ILogger<RetentionTieringService> logger)
    {
        _engine = engine;
        _verifier = verifier;
        _audit = audit;
        _notifications = notifications;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Retention scheduler started (tick {Tick}).", _options.TickInterval);

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
                _logger.LogError(ex, "Retention tiering tick failed; retrying next interval.");
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

        _logger.LogInformation("Retention scheduler stopped.");
    }

    /// <summary>One scheduler pass. Public so tests can drive it deterministically.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        int warmed = await _engine.TierToWarmBatchAsync(cancellationToken).ConfigureAwait(false);
        if (warmed > 0)
        {
            _logger.LogInformation("Retention: compressed {Count} event(s) Hot -> Warm.", warmed);
        }

        int exported = await _engine.ExportColdBatchAsync(cancellationToken).ConfigureAwait(false);
        if (exported > 0)
        {
            await AuditAsync(AuditActions.ArchiveCreated, $"{exported} event(s) exported to archive", cancellationToken).ConfigureAwait(false);
        }

        int expiredRestores = await _engine.ExpireRestoresBatchAsync(_options.RestoreExpiryBatchSize, cancellationToken).ConfigureAwait(false);
        if (expiredRestores > 0)
        {
            await AuditAsync(AuditActions.ArchiveRestoreExpired, $"{expiredRestores} restored event(s) expired", cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ArchiveVerification> verifications = await _verifier
            .VerifyDueBatchAsync(_options.VerificationStaleAfter, _options.VerificationBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (ArchiveVerification result in verifications.Where(v => v.Status != ArchiveStatus.Ok))
        {
            string action = result.Status == ArchiveStatus.TamperDetected ? AuditActions.ArchiveTamperDetected : AuditActions.ArchiveDeleted;
            await AuditAsync(action, $"archive '{result.StreamName}' (#{result.ArchiveId}): {result.Detail}", cancellationToken).ConfigureAwait(false);
            await _notifications(
                NotificationLevel.Critical,
                $"Archive integrity problem: {result.StreamName}",
                result.Detail ?? result.Status.ToString(),
                null,
                cancellationToken).ConfigureAwait(false);
        }

        int purged = await _engine.PurgeExpiredArchivesBatchAsync(_options.PurgeBatchSize, cancellationToken).ConfigureAwait(false);
        if (purged > 0)
        {
            await AuditAsync(AuditActions.ArchiveDeleted, $"{purged} archive(s) purged past their Delete horizon", cancellationToken).ConfigureAwait(false);
        }
    }

    private Task AuditAsync(string action, string detail, CancellationToken cancellationToken) =>
        _audit.AppendAsync(new AuditEntry(action, Actor: "retention-engine", Detail: detail), CancellationToken.None);
}
