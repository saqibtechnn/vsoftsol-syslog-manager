using System.Globalization;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Retention;

/// <summary>The result of a retention admin operation.</summary>
public sealed record RetentionActionResult(bool Ok, string Message, IReadOnlyList<string> Errors)
{
    public static RetentionActionResult Success(string message) => new(true, message, []);

    public static RetentionActionResult Fail(string message, IReadOnlyList<string>? errors = null) => new(false, message, errors ?? []);
}

/// <summary>
/// Retention policy configuration, the projected disk-usage estimate, and archive/restore
/// operations for the Settings and Archives screens (PHASE_10). Policy editing is
/// Administrator-only (<c>AuthPolicies.Administer</c>, checked by the page); archive
/// listing and restore requests use <c>AuthPolicies.ViewAudit</c> (Administrator + Auditor —
/// <see cref="VSoftSol.Syslog.Core.Enums.Role.Auditor"/>'s own docstring: "read-only across
/// audit log, reports, and archived data for compliance").
/// </summary>
public sealed class RetentionAdminService
{
    private readonly SqliteRetentionPolicyStore _policies;
    private readonly SqliteArchiveStore _archives;
    private readonly SqliteRestoreStore _restores;
    private readonly SqliteRetentionEngine _engine;
    private readonly RetentionEstimateReader _estimates;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;

    public RetentionAdminService(
        SqliteRetentionPolicyStore policies, SqliteArchiveStore archives, SqliteRestoreStore restores,
        SqliteRetentionEngine engine, RetentionEstimateReader estimates, SqliteAuditLog audit, CurrentUserAccessor users)
    {
        _policies = policies;
        _archives = archives;
        _restores = restores;
        _engine = engine;
        _estimates = estimates;
        _audit = audit;
        _users = users;
    }

    public Task<RetentionSettings> GetSettingsAsync(CancellationToken ct) => _policies.GetSettingsAsync(ct);

    public Task<IReadOnlyList<RetentionPolicy>> ListPoliciesAsync(CancellationToken ct) => _policies.ListPoliciesAsync(ct);

    public Task<RetentionPolicy?> GetPolicyAsync(long streamId, CancellationToken ct) => _policies.GetPolicyAsync(streamId, ct);

    /// <summary>The live-measured estimate for a candidate policy (UX_STANDARDS.md: never
    /// let a user configure retention blind).</summary>
    public async Task<RetentionEstimate> EstimateAsync(int hotDays, int warmDays, int coldDays, CancellationToken ct)
    {
        RetentionInputs inputs = await _estimates.MeasureAsync(ct).ConfigureAwait(false);
        var policy = new RetentionPolicy { HotDays = hotDays, WarmDays = warmDays, ColdDays = coldDays };
        return RetentionEstimator.Estimate(policy, inputs.EventsPerDay, inputs.AvgHotEventBytes, inputs.ObservedCompressionRatio);
    }

    public async Task<RetentionActionResult> SaveSettingsAsync(RetentionSettings settings, CancellationToken ct)
    {
        RetentionValidationResult validation = RetentionValidator.Validate(settings);
        if (!validation.Ok)
        {
            return RetentionActionResult.Fail("The retention defaults have a problem.", validation.Errors);
        }

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        await _policies.SaveSettingsAsync(settings, user.UserName, ct).ConfigureAwait(false);
        await AuditAsync("global defaults", user).ConfigureAwait(false);
        return RetentionActionResult.Success("Retention defaults saved.");
    }

    public async Task<RetentionActionResult> SavePolicyAsync(RetentionPolicy policy, CancellationToken ct)
    {
        RetentionValidationResult validation = RetentionValidator.Validate(policy);
        if (!validation.Ok)
        {
            return RetentionActionResult.Fail("That policy has a problem.", validation.Errors);
        }

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        await _policies.SavePolicyAsync(policy, user.UserName, ct).ConfigureAwait(false);
        await AuditAsync($"stream #{policy.StreamId}", user).ConfigureAwait(false);
        return RetentionActionResult.Success("Stream policy saved.");
    }

    public async Task<RetentionActionResult> DeletePolicyAsync(long streamId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        bool deleted = await _policies.DeletePolicyAsync(streamId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            return RetentionActionResult.Fail("That stream has no override to remove.");
        }

        await AuditAsync($"removed stream #{streamId} override", user).ConfigureAwait(false);
        return RetentionActionResult.Success("Reverted to the global defaults.");
    }

    public Task<IReadOnlyList<Core.Retention.ArchiveRecord>> ListArchivesAsync(CancellationToken ct) =>
        _archives.ListAsync(null, ct);

    public Task<IReadOnlyList<RestoreRecord>> ListActiveRestoresAsync(CancellationToken ct) => _restores.ListActiveAsync(ct);

    public async Task<RetentionActionResult> RequestRestoreAsync(long archiveId, int expiryDays, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        try
        {
            RestoreRecord restore = await _engine
                .RestoreArchiveAsync(archiveId, user.UserName, TimeSpan.FromDays(Math.Clamp(expiryDays, 1, 30)), ct)
                .ConfigureAwait(false);

            await _audit.AppendAsync(
                new AuditEntry(AuditActions.ArchiveRestoreRequested, user.UserName, "archive",
                    archiveId.ToString(CultureInfo.InvariantCulture), Detail: $"restore #{restore.RestoreId}, {restore.EventCount} event(s), expires {restore.ExpiresUtc:u}"),
                CancellationToken.None).ConfigureAwait(false);

            return RetentionActionResult.Success($"Restored {restore.EventCount} event(s) — searchable until {restore.ExpiresUtc.ToLocalTime():g}.");
        }
        catch (InvalidDataException ex)
        {
            return RetentionActionResult.Fail($"That archive failed integrity verification and cannot be restored: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return RetentionActionResult.Fail(ex.Message);
        }
    }

    private Task AuditAsync(string detail, CurrentUser user) =>
        _audit.AppendAsync(new AuditEntry(AuditActions.RetentionPolicyChange, user.UserName, "retention", null, Detail: detail), CancellationToken.None);
}
