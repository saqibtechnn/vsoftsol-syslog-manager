using System.Globalization;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>Result of a user-management operation, surfaced to the admin as a toast.</summary>
public sealed record AdminResult(bool Ok, string Message)
{
    public static AdminResult Success(string message) => new(true, message);

    public static AdminResult Fail(string message) => new(false, message);
}

/// <summary>
/// User CRUD for the Settings → Users screen (PHASE_04). Every change is audited with a
/// redacted before/after diff; the last enabled administrator cannot be disabled, demoted,
/// or deleted (fail closed).
/// </summary>
public sealed class UserAdminService
{
    private readonly SqliteUserStore _users;
    private readonly SqliteSessionStore _sessions;
    private readonly SqliteAuditLog _audit;
    private readonly IPasswordHasher _hasher;
    private readonly TimeProvider _time;

    public UserAdminService(
        SqliteUserStore users,
        SqliteSessionStore sessions,
        SqliteAuditLog audit,
        IPasswordHasher hasher,
        TimeProvider timeProvider)
    {
        _users = users;
        _sessions = sessions;
        _audit = audit;
        _hasher = hasher;
        _time = timeProvider;
    }

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct) => _users.ListAsync(ct);

    public async Task<AdminResult> CreateAsync(
        string actor, string username, string displayName, Role role, string temporaryPassword,
        IReadOnlyList<long> streamIds, IReadOnlyList<long> deviceGroupIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return AdminResult.Fail("Enter a username.");
        }

        if (await _users.FindByUsernameAsync(username, ct).ConfigureAwait(false) is not null)
        {
            return AdminResult.Fail($"A user named \"{username}\" already exists.");
        }

        if ((temporaryPassword ?? string.Empty).Length < 12)
        {
            return AdminResult.Fail("The temporary password must be at least 12 characters.");
        }

        long id = await _users.CreateAsync(
            username, displayName, role, _hasher.Hash(temporaryPassword!), mustChangePassword: true, _time.GetUtcNow(), ct)
            .ConfigureAwait(false);
        await _users.SetScopesAsync(id, streamIds, deviceGroupIds, ct).ConfigureAwait(false);

        await _audit.AppendAsync(new AuditEntry(
            AuditActions.UserCreate, actor, "user", id.ToString(CultureInfo.InvariantCulture),
            AfterJson: AuditDiff.Snapshot(new { username, displayName, role = role.ToString(), streamIds, deviceGroupIds })), ct)
            .ConfigureAwait(false);

        return AdminResult.Success($"Created {username}. They must set a new password at first sign-in.");
    }

    public async Task<AdminResult> UpdateAsync(
        string actor, long userId, string displayName, Role role, bool enabled,
        IReadOnlyList<long> streamIds, IReadOnlyList<long> deviceGroupIds, CancellationToken ct)
    {
        UserAccount? before = await _users.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (before is null)
        {
            return AdminResult.Fail("That user no longer exists.");
        }

        bool losingLastAdmin =
            before is { Role: Role.Administrator, IsEnabled: true } &&
            (role != Role.Administrator || !enabled) &&
            await _users.CountEnabledAdministratorsAsync(ct).ConfigureAwait(false) <= 1;
        if (losingLastAdmin)
        {
            return AdminResult.Fail("This is the last enabled administrator — promote another user first.");
        }

        await _users.UpdateProfileAsync(userId, displayName, role, enabled, ct).ConfigureAwait(false);
        await _users.SetScopesAsync(userId, streamIds, deviceGroupIds, ct).ConfigureAwait(false);
        if (!enabled)
        {
            await _sessions.RevokeAllForUserAsync(userId, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }

        await _audit.AppendAsync(new AuditEntry(
            AuditActions.UserUpdate, actor, "user", userId.ToString(CultureInfo.InvariantCulture),
            BeforeJson: AuditDiff.Snapshot(new { before.DisplayName, Role = before.Role.ToString(), before.IsEnabled, before.VisibleStreamIds, before.VisibleDeviceGroupIds }),
            AfterJson: AuditDiff.Snapshot(new { DisplayName = displayName, Role = role.ToString(), IsEnabled = enabled, VisibleStreamIds = streamIds, VisibleDeviceGroupIds = deviceGroupIds })), ct)
            .ConfigureAwait(false);

        return AdminResult.Success($"Saved changes to {before.Username}.");
    }

    public async Task<AdminResult> ResetPasswordAsync(string actor, long userId, string temporaryPassword, CancellationToken ct)
    {
        if ((temporaryPassword ?? string.Empty).Length < 12)
        {
            return AdminResult.Fail("The temporary password must be at least 12 characters.");
        }

        UserAccount? user = await _users.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return AdminResult.Fail("That user no longer exists.");
        }

        await _users.SetPasswordAsync(userId, _hasher.Hash(temporaryPassword!), mustChangePassword: true, _time.GetUtcNow(), ct)
            .ConfigureAwait(false);
        await _sessions.RevokeAllForUserAsync(userId, _time.GetUtcNow(), ct).ConfigureAwait(false);

        await _audit.AppendAsync(new AuditEntry(
            AuditActions.PasswordReset, actor, "user", userId.ToString(CultureInfo.InvariantCulture),
            Detail: "administrative reset; user must change at next sign-in"), ct).ConfigureAwait(false);

        return AdminResult.Success($"Reset {user.Username}'s password and signed them out everywhere.");
    }
}
