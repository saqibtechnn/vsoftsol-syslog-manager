using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Data.Users;

/// <summary>A row of the <c>users</c> table plus its resolved role, as read by the store.</summary>
public sealed record UserAccount
{
    public required long UserId { get; init; }

    public required string Username { get; init; }

    public required string DisplayName { get; init; }

    public required Role Role { get; init; }

    /// <summary>PHC-format Argon2id hash, or null when the first-run wizard has not set it yet.</summary>
    public string? PasswordHash { get; init; }

    public bool MustChangePassword { get; init; }

    public bool IsEnabled { get; init; }

    public int FailedLoginCount { get; init; }

    public DateTimeOffset? LockedUntilUtc { get; init; }

    public DateTimeOffset? LastLoginUtc { get; init; }

    public DateTimeOffset? PasswordChangedUtc { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>TOTP MFA (PHASE_11 item 8). The secret itself lives in the DPAPI-protected
    /// <c>secrets</c> table, keyed "mfa.totp.&lt;user_id&gt;" — never here.</summary>
    public bool MfaEnabled { get; init; }

    public DateTimeOffset? MfaEnrolledUtc { get; init; }

    /// <summary>Streams this user may see; empty means all (see <c>UserScope</c>).</summary>
    public IReadOnlyList<long> VisibleStreamIds { get; init; } = [];

    /// <summary>Device groups this user may see; empty means all.</summary>
    public IReadOnlyList<long> VisibleDeviceGroupIds { get; init; } = [];

    public bool IsLockedAt(DateTimeOffset nowUtc) => LockedUntilUtc is { } until && until > nowUtc;
}
