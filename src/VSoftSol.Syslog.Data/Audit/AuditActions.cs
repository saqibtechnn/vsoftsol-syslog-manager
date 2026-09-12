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
    public const string FirstRunSetupComplete = "firstrun.setup.complete";

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

    public const string AlertCreate = "alert.create";
    public const string AlertUpdate = "alert.update";
    public const string AlertDelete = "alert.delete";
    public const string AlertEnable = "alert.enable";
    public const string AlertDisable = "alert.disable";
    public const string AlertFired = "alert.fired";
    public const string AlertAcknowledged = "alert.acknowledged";
    public const string AlertResolved = "alert.resolved";
    public const string AlertAutoResolved = "alert.autoresolved";
    public const string AlertRenotified = "alert.renotified";
    public const string AlertEvaluationMissed = "alert.evaluation.missed";

    public const string DashboardCreate = "dashboard.create";
    public const string DashboardUpdate = "dashboard.update";
    public const string DashboardDelete = "dashboard.delete";
    public const string DashboardCopy = "dashboard.copy";

    public const string RetentionPolicyChange = "retention.policy.change";
    public const string ArchiveCreated = "archive.created";
    public const string ArchiveVerified = "archive.verified";
    public const string ArchiveTamperDetected = "archive.tamper_detected";
    public const string ArchiveDeleted = "archive.deleted";
    public const string ArchiveRestoreRequested = "archive.restore.requested";
    public const string ArchiveRestoreExpired = "archive.restore.expired";

    public const string ReportCreate = "report.create";
    public const string ReportUpdate = "report.update";
    public const string ReportDelete = "report.delete";
    public const string ReportRun = "report.run";
    public const string ReportDelivered = "report.delivered";
    public const string ReportDeliveryFailed = "report.delivery.failed";
}
