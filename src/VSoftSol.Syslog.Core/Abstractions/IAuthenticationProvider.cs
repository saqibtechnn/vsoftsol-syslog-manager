namespace VSoftSol.Syslog.Core.Abstractions;

/// <summary>
/// Authentication seam. One of only two seams in the product (CLAUDE.md "Two seams
/// only"). The v1 implementation is a local user store with Argon2id hashing
/// (<c>LocalAuthenticationProvider</c>, Phase 4); this interface exists so a future
/// Active Directory / LDAP provider can replace it without changing the UI or the
/// authorization layer.
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>
    /// Verify a username/password pair. Implementations must take a constant-time path
    /// for unknown users versus bad passwords, and must apply account lockout.
    /// </summary>
    Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-load the current scope and status for an already-authenticated user, so a
    /// long-lived session picks up role or scope changes. Returns null if the user no
    /// longer exists or is disabled.
    /// </summary>
    Task<AuthenticatedUser?> RefreshAsync(string username, CancellationToken cancellationToken);

    /// <summary>
    /// True when this provider owns password lifecycle (the local store) and false when
    /// an external directory does (AD). The UI hides password-change screens when false.
    /// </summary>
    bool SupportsPasswordChange { get; }
}
