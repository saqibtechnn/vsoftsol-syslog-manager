using System.Globalization;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Reports;

/// <summary>The result of a report admin operation.</summary>
public sealed record ReportActionResult(bool Ok, string Message, IReadOnlyList<string> Errors, long ReportId = 0)
{
    public static ReportActionResult Success(string message, long id = 0) => new(true, message, [], id);

    public static ReportActionResult Fail(string message, IReadOnlyList<string>? errors = null) => new(false, message, errors ?? []);
}

/// <summary>
/// Report CRUD + copy from the UI (PHASE_10 build items 6/8). Every authenticated user with
/// report access (<c>AuthPolicies.ViewReports</c> — all four roles) may view, run, copy, and
/// schedule reports; a system (canned/compliance) report is never editable or deletable —
/// "Copy" produces an owned, schedulable clone. This is deliberately more permissive than
/// <c>DashboardService</c>'s Operate-only mutation gate: PHASE_10's own Definition of Done
/// requires an Auditor to complete "generate a 90-day PCI-DSS report and schedule it monthly
/// by email" unaided, and <c>Role.Auditor</c>'s docstring ("read-only across audit log,
/// reports, and archived data") already anticipates broad report access.
/// </summary>
public sealed class ReportAdminService
{
    private readonly SqliteReportStore _store;
    private readonly SqliteSavedSearchStore _savedSearches;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;
    private readonly SqliteUserStore _userStore;

    public ReportAdminService(
        SqliteReportStore store, SqliteSavedSearchStore savedSearches, SqliteAuditLog audit,
        CurrentUserAccessor users, SqliteUserStore userStore)
    {
        _store = store;
        _savedSearches = savedSearches;
        _audit = audit;
        _users = users;
        _userStore = userStore;
    }

    public static IReadOnlyList<ReportTemplate> Templates => CannedReportCatalog.All;

    public async Task<IReadOnlyList<ReportDefinition>> ListAsync(CancellationToken ct)
    {
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);
        return await _store.ListForUserAsync(uid, ct).ConfigureAwait(false);
    }

    public async Task<ReportDefinition?> GetAsync(long id, CancellationToken ct)
    {
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);
        return await _store.GetAsync(id, uid, ct).ConfigureAwait(false);
    }

    public async Task<ReportActionResult> SaveAsync(ReportDefinition report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.IsSystem)
        {
            return ReportActionResult.Fail("A shipped report template cannot be edited. Use “Copy to schedule”.");
        }

        ReportValidationResult validation = ReportValidator.Validate(report);
        if (!validation.Ok)
        {
            return ReportActionResult.Fail("The report has a problem.", validation.Errors);
        }

        if (report.IsCustom && report.SavedSearchId is { } sid
            && await _savedSearches.GetQueryTextAsync(sid, ct).ConfigureAwait(false) is null)
        {
            return ReportActionResult.Fail("That saved search no longer exists.");
        }

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);

        if (report.Id == 0)
        {
            long id = await _store.CreateAsync(report, uid, isSystem: false, user.UserName, ct).ConfigureAwait(false);
            await AuditAsync(AuditActions.ReportCreate, id, report.Name, user).ConfigureAwait(false);
            return ReportActionResult.Success("Report created.", id);
        }

        if (!await _store.UpdateAsync(report, uid, user.UserName, ct).ConfigureAwait(false))
        {
            return ReportActionResult.Fail("That report no longer exists, or you do not own it.");
        }

        await AuditAsync(AuditActions.ReportUpdate, report.Id, report.Name, user).ConfigureAwait(false);
        return ReportActionResult.Success("Report saved.", report.Id);
    }

    public async Task<ReportActionResult> DeleteAsync(long id, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);

        if (!await _store.DeleteAsync(id, uid, ct).ConfigureAwait(false))
        {
            return ReportActionResult.Fail("That report cannot be deleted (a shipped template, or not yours).");
        }

        await AuditAsync(AuditActions.ReportDelete, id, string.Empty, user).ConfigureAwait(false);
        return ReportActionResult.Success("Report deleted.");
    }

    /// <summary>Copies a system template (or another visible report) into an owned,
    /// schedulable clone — the "pick-and-run, then optionally schedule" flow the UX gate
    /// requires.</summary>
    public async Task<ReportActionResult> CopyAsync(long sourceId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);

        ReportDefinition? source = await _store.GetAsync(sourceId, uid, ct).ConfigureAwait(false);
        if (source is null)
        {
            return ReportActionResult.Fail("That report is not available to copy.");
        }

        var copy = source with
        {
            Id = 0,
            OwnerUserId = uid,
            IsSystem = false,
            Name = await UniqueNameAsync(uid, $"{source.Name} (copy)", ct).ConfigureAwait(false),
        };

        long id = await _store.CreateAsync(copy, uid, isSystem: false, user.UserName, ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.ReportCreate, id, $"copied from '{source.Name}'", user).ConfigureAwait(false);
        return ReportActionResult.Success($"Copied to “{copy.Name}” — now yours to schedule.", id);
    }

    private async Task<string> UniqueNameAsync(long uid, string baseName, CancellationToken ct)
    {
        var taken = (await _store.ListForUserAsync(uid, ct).ConfigureAwait(false))
            .Where(r => r.OwnerUserId == uid)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} {Guid.NewGuid():N}";
    }

    private async Task<long> CurrentUserIdAsync(CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        UserAccount? account = await _userStore.FindByUsernameAsync(user.UserName, ct).ConfigureAwait(false);
        return account?.UserId ?? -1;
    }

    private Task AuditAsync(string action, long id, string name, CurrentUser user) =>
        _audit.AppendAsync(
            new AuditEntry(action, user.UserName, "report", id.ToString(CultureInfo.InvariantCulture), Detail: name),
            CancellationToken.None);
}
