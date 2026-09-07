namespace VSoftSol.Syslog.Data.Audit;

/// <summary>
/// The canonical audit action verbs. Using constants (not free strings at the call site)
/// keeps the audit vocabulary stable and greppable across phases.
/// </summary>
public static class AuditActions
{
    public const string LoginSuccess = "login.success";
    public const string LoginFailure = "login.failure";
    public const string LoginLockout = "login.lockout";
    public const string Logout = "logout";
    public const string SessionRevoked = "session.revoked";
    public const string SessionExpired = "session.expired";

    public const string PasswordChange = "password.change";
    public const string PasswordReset = "password.reset";

    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string UserDisable = "user.disable";
    public const string UserEnable = "user.enable";
    public const string UserScopeChange = "user.scope.change";

    public const string ConfigChange = "config.change";
    public const string SecretChange = "secret.change";
    public const string Export = "export";
}
