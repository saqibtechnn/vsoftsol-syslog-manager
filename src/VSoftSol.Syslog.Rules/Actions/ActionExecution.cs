using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Resolves a secret by name (the Phase 4 DPAPI store, injected by the composition root).
/// A rule action carries only the <em>name</em>; the value is fetched here at execute time
/// and never persisted, logged, or exported (SECURITY_STANDARDS.md).
/// </summary>
public delegate ValueTask<string?> SecretResolver(string name, CancellationToken cancellationToken);

/// <summary>Raises a UI notification (backed by <c>SqliteNotificationStore</c>).</summary>
public delegate ValueTask NotificationSink(
    NotificationLevel level, string title, string body, long? ruleId, CancellationToken cancellationToken);

/// <summary>
/// Host-supplied limits for the side-effecting executors (PHASE_07 security). Bound from
/// the <c>Rules:Actions</c> config section by the composition root — never by a rule author.
/// </summary>
public sealed class ActionExecutorOptions
{
    public const string SectionName = "Rules:Actions";

    /// <summary>Absolute directories a <c>RunScript</c> executable may live under. Empty ⇒ scripts disabled.</summary>
    public List<string> ScriptAllowListDirectories { get; set; } = [];

    /// <summary>Absolute base directory <c>WriteToFile</c> is confined to. Null ⇒ file writes disabled.</summary>
    public string? FileActionBaseDirectory { get; set; }

    /// <summary>The collector's own listener endpoints (<c>host:port</c>) — a forward target here is a loop.</summary>
    public List<string> LocalSyslogEndpoints { get; set; } = [];

    /// <summary>CIDR ranges a webhook may reach despite being private/link-local. Explicit opt-in.</summary>
    public List<string> WebhookPrivateNetworkAllowList { get; set; } = [];

    /// <summary>Hard cap on a webhook / ODBC / script timeout regardless of the rule's setting.</summary>
    public int MaxActionTimeoutSeconds { get; set; } = 120;

    /// <summary>Bytes of script stdout/stderr and webhook response body kept for the audit log.</summary>
    public int MaxCapturedBytes { get; set; } = 64 * 1024;
}

/// <summary>Everything an executor needs for one action run.</summary>
public sealed record ActionContext(
    long RuleId,
    string RuleName,
    RuleAction Action,
    SyslogEvent Event,
    SecretResolver Secrets,
    NotificationSink Notifications,
    ActionExecutorOptions Options);

/// <summary>The outcome of one action run.</summary>
/// <param name="Ok">True on success.</param>
/// <param name="Detail">A short human summary for the audit log (never a secret).</param>
/// <param name="Retryable">
/// True ⇒ a transient failure the dispatcher should back off and retry; false ⇒ a permanent
/// failure (bad config, refused by policy) that goes straight to the dead-letter state.
/// </param>
public sealed record ActionResult(bool Ok, string Detail, bool Retryable)
{
    public static ActionResult Success(string detail) => new(true, detail, false);

    public static ActionResult Transient(string detail) => new(false, detail, true);

    public static ActionResult Permanent(string detail) => new(false, detail, false);
}

/// <summary>Executes one kind of <see cref="RuleAction"/>. Internal — a closed set, not a seam.</summary>
internal interface IActionExecutor
{
    ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken);
}
