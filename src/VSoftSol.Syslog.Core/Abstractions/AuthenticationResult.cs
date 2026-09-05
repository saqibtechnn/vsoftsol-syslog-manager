namespace VSoftSol.Syslog.Core.Abstractions;

/// <summary>Why an authentication attempt did not succeed. <see cref="None"/> means it did.</summary>
public enum AuthenticationFailureReason
{
    None = 0,
    UnknownUser = 1,
    BadPassword = 2,
    AccountLocked = 3,
    AccountDisabled = 4,
    ProviderUnavailable = 5,
}

/// <summary>
/// Outcome of <see cref="IAuthenticationProvider.AuthenticateAsync"/>. Callers must not
/// distinguish <see cref="AuthenticationFailureReason.UnknownUser"/> from
/// <see cref="AuthenticationFailureReason.BadPassword"/> in any user-facing message
/// (username enumeration); the distinction exists for the audit log only.
/// </summary>
public sealed record AuthenticationResult
{
    private AuthenticationResult(bool succeeded, AuthenticationFailureReason reason, AuthenticatedUser? user)
    {
        Succeeded = succeeded;
        FailureReason = reason;
        User = user;
    }

    public bool Succeeded { get; }

    public AuthenticationFailureReason FailureReason { get; }

    public AuthenticatedUser? User { get; }

    public static AuthenticationResult Success(AuthenticatedUser user) =>
        new(true, AuthenticationFailureReason.None, user);

    public static AuthenticationResult Failure(AuthenticationFailureReason reason) =>
        new(false, reason, null);
}
