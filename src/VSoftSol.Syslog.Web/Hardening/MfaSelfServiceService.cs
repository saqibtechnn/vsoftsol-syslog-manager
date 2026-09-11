using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Security.Mfa;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Hardening;

/// <summary>
/// Self-service TOTP MFA enrollment (PHASE_11 item 8) for the signed-in user. Every
/// authenticated user may enroll — the phase scopes MFA to Administrator accounts by
/// applicability, not by hiding the feature from anyone else. The secret is never shown
/// again after enrollment completes; recovery codes are shown exactly once.
/// </summary>
public sealed class MfaSelfServiceService
{
    private const string SecretPrefix = "mfa.totp.";
    private const int RecoveryCodeCount = 10;

    private readonly SqliteUserStore _users;
    private readonly SqliteSecretStore _secrets;
    private readonly SqliteMfaRecoveryCodeStore _recoveryCodes;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _currentUser;
    private readonly TimeProvider _time;

    public MfaSelfServiceService(
        SqliteUserStore users, SqliteSecretStore secrets, SqliteMfaRecoveryCodeStore recoveryCodes,
        SqliteAuditLog audit, CurrentUserAccessor currentUser, TimeProvider? time = null)
    {
        _users = users;
        _secrets = secrets;
        _recoveryCodes = recoveryCodes;
        _audit = audit;
        _currentUser = currentUser;
        _time = time ?? TimeProvider.System;
    }

    public sealed record EnrollmentStart(string Base32Secret, string AccountLabel, string Issuer);

    public async Task<UserAccount> GetCurrentUserAsync(CancellationToken ct)
    {
        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        return await _users.FindByUsernameAsync(me.UserName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Signed-in user was not found.");
    }

    /// <summary>Generates a new secret and stores it (not yet enforced — <see cref="VerifyAndEnableAsync"/>
    /// must succeed first). Calling this again before verifying restarts enrollment with a fresh secret.</summary>
    public async Task<EnrollmentStart> StartEnrollmentAsync(CancellationToken ct)
    {
        UserAccount user = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        byte[] secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        string base32 = Base32.Encode(secret);
        await _secrets.SetAsync(SecretPrefix + user.UserId, base32, user.Username, ct).ConfigureAwait(false);
        return new EnrollmentStart(base32, user.Username, BrandingInfo.ProductName);
    }

    /// <summary>Verifies the enrollment code and, on success, turns MFA on and issues ten
    /// recovery codes (shown once, by the caller, then discarded).</summary>
    public async Task<(bool Ok, string Message, IReadOnlyList<string>? RecoveryCodes)> VerifyAndEnableAsync(string code, CancellationToken ct)
    {
        UserAccount user = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        string? base32 = await _secrets.GetAsync(SecretPrefix + user.UserId, ct).ConfigureAwait(false);
        if (base32 is null)
        {
            return (false, "Start enrollment first.", null);
        }

        if (!TotpGenerator.ValidateCode(Base32.Decode(base32), code, _time.GetUtcNow()))
        {
            return (false, "That code is not correct or has expired. Check your authenticator app's clock and try again.", null);
        }

        IReadOnlyList<string> codes = RecoveryCodeGenerator.Generate(RecoveryCodeCount);
        await _recoveryCodes.ReplaceAllAsync(user.UserId, [.. codes.Select(RecoveryCodeGenerator.Hash)], _time.GetUtcNow(), ct).ConfigureAwait(false);
        await _users.SetMfaEnabledAsync(user.UserId, true, _time.GetUtcNow(), ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.SecretChange, user.Username, "mfa", user.UserId.ToString(), Detail: "MFA enabled"), CancellationToken.None)
            .ConfigureAwait(false);

        return (true, "MFA is now enabled on your account. Save these recovery codes somewhere safe — they will not be shown again.", codes);
    }

    public async Task<(bool Ok, string Message)> DisableAsync(CancellationToken ct)
    {
        UserAccount user = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        await _secrets.DeleteAsync(SecretPrefix + user.UserId, ct).ConfigureAwait(false);
        await _recoveryCodes.ReplaceAllAsync(user.UserId, [], _time.GetUtcNow(), ct).ConfigureAwait(false);
        await _users.SetMfaEnabledAsync(user.UserId, false, null, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.SecretChange, user.Username, "mfa", user.UserId.ToString(), Detail: "MFA disabled"), CancellationToken.None)
            .ConfigureAwait(false);
        return (true, "MFA has been turned off for your account.");
    }

    /// <summary>Verifies a login-time code against either the TOTP secret or an unused
    /// recovery code (consuming it on success). Not yet wired into the login flow itself —
    /// see docs/evidence/phase-11/known-issues.md (B11-3).</summary>
    public async Task<bool> VerifyLoginCodeAsync(long userId, string code, CancellationToken ct)
    {
        string? base32 = await _secrets.GetAsync(SecretPrefix + userId, ct).ConfigureAwait(false);
        if (base32 is not null && TotpGenerator.ValidateCode(Base32.Decode(base32), code, _time.GetUtcNow()))
        {
            return true;
        }

        return await _recoveryCodes.TryConsumeAsync(userId, RecoveryCodeGenerator.Hash(code), _time.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
