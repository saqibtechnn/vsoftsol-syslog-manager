using System.Globalization;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Dashboards;

/// <summary>The result of a dashboard admin operation.</summary>
public sealed record DashboardActionResult(bool Ok, string Message, IReadOnlyList<string> Errors, long DashboardId = 0)
{
    public static DashboardActionResult Success(string message, long id = 0) => new(true, message, [], id);

    public static DashboardActionResult Fail(string message, IReadOnlyList<string>? errors = null) =>
        new(false, message, errors ?? []);
}

/// <summary>
/// Dashboard CRUD + copy from the UI (PHASE_09). Role is checked <b>at the service</b>:
/// every authenticated user can view and copy; only Administrator / Operator can create,
/// edit, or delete their own dashboards; nobody can edit or delete a shipped system
/// dashboard. Every mutation is validated with <see cref="DashboardValidator"/> — the same
/// check the picker enforces — and audited.
/// </summary>
public sealed class DashboardService
{
    private readonly SqliteDashboardStore _store;
    private readonly SqliteSavedSearchStore _savedSearches;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;
    private readonly SqliteUserStore _userStore;

    public DashboardService(
        SqliteDashboardStore store,
        SqliteSavedSearchStore savedSearches,
        SqliteAuditLog audit,
        CurrentUserAccessor users,
        SqliteUserStore userStore)
    {
        _store = store;
        _savedSearches = savedSearches;
        _audit = audit;
        _users = users;
        _userStore = userStore;
    }

    public static IReadOnlyList<DashboardDefinition> Templates => DefaultDashboards.All;

    public async Task<IReadOnlyList<DashboardDefinition>> ListAsync(CancellationToken ct)
    {
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);
        return await _store.ListForUserAsync(uid, ct).ConfigureAwait(false);
    }

    public async Task<DashboardDefinition?> GetAsync(long id, CancellationToken ct)
    {
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);
        return await _store.GetAsync(id, uid, ct).ConfigureAwait(false);
    }

    public async Task<DashboardActionResult> SaveAsync(DashboardDefinition dashboard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return DashboardActionResult.Fail("You do not have permission to change dashboards.");
        }

        if (dashboard.IsSystem)
        {
            return DashboardActionResult.Fail("A shipped dashboard cannot be edited. Use “Copy to edit”.");
        }

        ValidationResult validation = DashboardValidator.Validate(dashboard);
        if (!validation.Ok)
        {
            return DashboardActionResult.Fail("The dashboard has a problem.", validation.Errors);
        }

        IReadOnlyList<string> missing = await MissingSavedSearchesAsync(dashboard, ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            return DashboardActionResult.Fail("A widget refers to a saved search that no longer exists.", missing);
        }

        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);

        if (dashboard.Id == 0)
        {
            long id;
            try
            {
                id = await _store.CreateAsync(dashboard, uid, user.UserName, ct).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return DashboardActionResult.Fail("You already have a dashboard with that name.");
            }

            await Audit(AuditActions.DashboardCreate, id, dashboard.Name, user).ConfigureAwait(false);
            return DashboardActionResult.Success("Dashboard created.", id);
        }

        if (!await _store.UpdateAsync(dashboard, uid, user.UserName, ct).ConfigureAwait(false))
        {
            return DashboardActionResult.Fail("That dashboard no longer exists, or you do not own it.");
        }

        await Audit(AuditActions.DashboardUpdate, dashboard.Id, dashboard.Name, user).ConfigureAwait(false);
        return DashboardActionResult.Success("Dashboard saved.", dashboard.Id);
    }

    /// <summary>Layout-only save (drag-to-arrange), so a reposition does not re-validate every widget field.</summary>
    public async Task<DashboardActionResult> SaveLayoutAsync(long id, IReadOnlyList<WidgetLayout> layout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(layout);
        DashboardDefinition? current = await GetAsync(id, ct).ConfigureAwait(false);
        if (current is null)
        {
            return DashboardActionResult.Fail("That dashboard no longer exists.");
        }

        return await SaveAsync(current with { Layout = [.. layout.Select(l => l.Normalized())] }, ct).ConfigureAwait(false);
    }

    public async Task<DashboardActionResult> DeleteAsync(long id, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return DashboardActionResult.Fail("You do not have permission to delete dashboards.");
        }

        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);
        if (!await _store.DeleteAsync(id, uid, ct).ConfigureAwait(false))
        {
            return DashboardActionResult.Fail("That dashboard cannot be deleted (a shipped dashboard, or not yours).");
        }

        await Audit(AuditActions.DashboardDelete, id, string.Empty, user).ConfigureAwait(false);
        return DashboardActionResult.Success("Dashboard deleted.");
    }

    /// <summary>
    /// Copies a system or shared dashboard into an owned, editable one. Available to every
    /// authenticated user (PHASE_09 build item 6 — default dashboards are "copyable").
    /// </summary>
    public async Task<DashboardActionResult> CopyAsync(long sourceId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        long uid = await CurrentUserIdAsync(ct).ConfigureAwait(false);

        DashboardDefinition? source = await _store.GetAsync(sourceId, uid, ct).ConfigureAwait(false);
        if (source is null)
        {
            return DashboardActionResult.Fail("That dashboard is not available to copy.");
        }

        var copy = source with
        {
            Id = 0,
            OwnerUserId = uid,
            IsSystem = false,
            IsShared = false,
            SystemKey = null,
            Name = await UniqueNameAsync(uid, $"{source.Name} (copy)", ct).ConfigureAwait(false),
        };

        long id = await _store.CreateAsync(copy, uid, user.UserName, ct).ConfigureAwait(false);
        await Audit(AuditActions.DashboardCopy, id, $"from '{source.Name}'", user).ConfigureAwait(false);
        return DashboardActionResult.Success($"Copied to “{copy.Name}” — now yours to edit.", id);
    }

    private async Task<IReadOnlyList<string>> MissingSavedSearchesAsync(DashboardDefinition dashboard, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (WidgetDefinition widget in dashboard.Widgets)
        {
            if (widget.Source is { Kind: WidgetSourceKind.EventQuery, SavedSearchId: { } sid }
                && await _savedSearches.GetQueryTextAsync(sid, ct).ConfigureAwait(false) is null)
            {
                missing.Add($"Widget '{widget.Title}': saved search #{sid.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return missing;
    }

    private async Task<string> UniqueNameAsync(long uid, string baseName, CancellationToken ct)
    {
        var taken = (await _store.ListForUserAsync(uid, ct).ConfigureAwait(false))
            .Where(d => d.OwnedByCurrentUser)
            .Select(d => d.Name)
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

    private Task Audit(string action, long id, string name, CurrentUser user) =>
        _audit.AppendAsync(
            new AuditEntry(action, user.UserName, "dashboard",
                id.ToString(CultureInfo.InvariantCulture), Detail: name),
            CancellationToken.None);
}
