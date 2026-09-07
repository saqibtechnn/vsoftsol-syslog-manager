using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Security;

namespace VSoftSol.Syslog.Data.Users;

/// <summary>
/// The v1 <see cref="IAuthenticationProvider"/>: a local user store with Argon2id hashing,
/// account lockout, and a constant-time path for unknown users versus bad passwords
/// (PHASE_04 build item 2, SECURITY_STANDARDS.md "username enumeration via timing").
/// </summary>
public sealed class LocalAuthenticationProvider : IAuthenticationProvider
{
    // A fixed, well-formed hash of a random value. Verified against the supplied password
    // whenever the real account is missing / disabled / has no password set, so every
    // failure path spends one Argon2 computation and cannot be told apart by timing.
    private readonly string _decoyHash;

    private readonly SqliteUserStore _users;
    private readonly IPasswordHasher _hasher;
    private readonly AuthenticationOptions _options;
    private readonly TimeProvider _time;

    public LocalAuthenticationProvider(
        SqliteUserStore users,
        IPasswordHasher hasher,
        IOptions<AuthenticationOptions> options,
        TimeProvider? timeProvider = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
        _decoyHash = _hasher.Hash(Guid.NewGuid().ToString("N"));
    }

    public bool SupportsPasswordChange => true;

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        DateTimeOffset now = _time.GetUtcNow();
        UserAccount? user = await _users.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        // Unknown user: spend one hash against the decoy, then fail as UnknownUser.
        if (user is null)
        {
            _ = _hasher.Verify(password, _decoyHash, out _);
            return AuthenticationResult.Failure(AuthenticationFailureReason.UnknownUser);
        }

        bool lockedNow = user.IsLockedAt(now);
        string hashToCheck = user.PasswordHash ?? _decoyHash;
        bool passwordOk = _hasher.Verify(password, hashToCheck, out bool needsRehash) && user.PasswordHash is not null;

        if (!user.IsEnabled)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.AccountDisabled);
        }

        if (lockedNow)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.AccountLocked);
        }

        if (!passwordOk)
        {
            int failures = await _users.RecordLoginFailureAsync(
                user.UserId, now, _options.LockoutThreshold, _options.LockoutDuration, cancellationToken).ConfigureAwait(false);

            return AuthenticationResult.Failure(
                _options.LockoutThreshold > 0 && failures >= _options.LockoutThreshold
                    ? AuthenticationFailureReason.AccountLocked
                    : AuthenticationFailureReason.BadPassword);
        }

        await _users.RecordLoginSuccessAsync(user.UserId, now, cancellationToken).ConfigureAwait(false);

        if (needsRehash)
        {
            await _users.SetPasswordAsync(
                user.UserId, _hasher.Hash(password), user.MustChangePassword, now, cancellationToken).ConfigureAwait(false);
        }

        return AuthenticationResult.Success(ToAuthenticatedUser(user));
    }

    public async Task<AuthenticatedUser?> RefreshAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(username);
        UserAccount? user = await _users.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        return user is { IsEnabled: true } ? ToAuthenticatedUser(user) : null;
    }

    private static AuthenticatedUser ToAuthenticatedUser(UserAccount user) => new(
        user.Username,
        user.DisplayName,
        user.Role,
        user.VisibleStreamIds,
        user.VisibleDeviceGroupIds,
        user.MustChangePassword);
}
