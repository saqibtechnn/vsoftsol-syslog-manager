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

    public const string DeviceCreate = "device.create";
    public const string DeviceUpdate = "device.update";
    public const string DeviceApprove = "device.approve";
    public const string DeviceReject = "device.reject";
    public const string DeviceGroupChange = "device.group.change";

    public const string StreamCreate = "stream.create";
    public const string StreamUpdate = "stream.update";
    public const string StreamDelete = "stream.delete";

    public const string RuleCreate = "rule.create";
    public const string RuleUpdate = "rule.update";
    public const string RuleDelete = "rule.delete";
    public const string RuleEnable = "rule.enable";
    public const string RuleDisable = "rule.disable";

    public const string ActionFired = "action.fired";
    public const string ActionFailed = "action.failed";
    public const string ActionDeadLettered = "action.deadlettered";
    public const string ActionRefused = "action.refused";
    public const string ActionTested = "action.tested";

    public const string NotificationRaised = "notification.raised";
}
