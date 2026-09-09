using System.Text.Json.Serialization;

namespace VSoftSol.Syslog.Core.Rules;

/// <summary>
/// One action in a rule's ordered action list (PHASE_07 item 3). Actions are a closed set
/// of strategies — like <c>ConditionNode</c> (ADR 0014) and the vendor packs — modelled as
/// a polymorphic tree that round-trips to JSON in the <c>rules.actions_json</c> column.
/// Lives in <c>Core</c> so the ingest-path engine (<c>VSoftSol.Syslog.Rules</c>) and the
/// visual editor (<c>VSoftSol.Syslog.Web</c>) share one model (ADR 0015).
/// </summary>
/// <remarks>
/// Credentials are <b>never</b> stored on an action — only the <em>name</em> of a secret in
/// the Phase 4 DPAPI store (<see cref="SecretName"/>). A config-bundle export carries the
/// name, never the value.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SendEmailAction), "email")]
[JsonDerivedType(typeof(HttpWebhookAction), "webhook")]
[JsonDerivedType(typeof(RunScriptAction), "script")]
[JsonDerivedType(typeof(ForwardSyslogAction), "forward")]
[JsonDerivedType(typeof(WriteToFileAction), "file")]
[JsonDerivedType(typeof(WriteToOdbcAction), "odbc")]
[JsonDerivedType(typeof(AddTagAction), "tag")]
[JsonDerivedType(typeof(RouteToStreamAction), "route")]
[JsonDerivedType(typeof(SuppressAction), "suppress")]
[JsonDerivedType(typeof(RaiseNotificationAction), "notify")]
public abstract class RuleAction
{
    /// <summary>Stable id for the editor's drag-reorder; not otherwise significant.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Per-action rate limit and cool-down. Ignored for inline actions.</summary>
    public ActionThrottle Throttle { get; set; } = ActionThrottle.None;

    /// <summary>The name of a secret in the DPAPI store, when this action needs credentials.</summary>
    public string? SecretName { get; set; }
}

/// <summary>
/// Derived, non-serialised metadata about an action. Kept out of <see cref="RuleAction"/>
/// itself so nothing leaks into <c>actions_json</c> (an override of a <c>[JsonIgnore]</c>
/// abstract property is not reliably ignored by <c>System.Text.Json</c>).
/// </summary>
public static class RuleActionInfo
{
    /// <summary>True for actions with an external side effect (dispatched off the ingest thread).</summary>
    public static bool IsSideEffecting(RuleAction action) => action switch
    {
        AddTagAction or RouteToStreamAction or SuppressAction => false,
        _ => true,
    };

    /// <summary>A short human label for the editor and the audit log.</summary>
    public static string Label(RuleAction action) => action switch
    {
        SendEmailAction => "Send email",
        HttpWebhookAction => "HTTP webhook",
        RunScriptAction => "Run script",
        ForwardSyslogAction => "Forward syslog",
        WriteToFileAction => "Write to file",
        WriteToOdbcAction => "Write to ODBC",
        AddTagAction => "Add tag",
        RouteToStreamAction => "Route to stream",
        SuppressAction => "Suppress further rules",
        RaiseNotificationAction => "Raise notification",
        _ => action.GetType().Name,
    };

    /// <summary>The polymorphic discriminator token, for the outbox <c>kind</c> column.</summary>
    public static string Kind(RuleAction action) => action switch
    {
        SendEmailAction => "email",
        HttpWebhookAction => "webhook",
        RunScriptAction => "script",
        ForwardSyslogAction => "forward",
        WriteToFileAction => "file",
        WriteToOdbcAction => "odbc",
        AddTagAction => "tag",
        RouteToStreamAction => "route",
        SuppressAction => "suppress",
        RaiseNotificationAction => "notify",
        _ => "unknown",
    };
}

/// <summary>Send an email over SMTP with a templated subject and body (PHASE_07 item 3).</summary>
public sealed class SendEmailAction : RuleAction
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseTls { get; set; } = true;

    /// <summary>Login name; the password is <see cref="RuleAction.SecretName"/>. Blank = anonymous.</summary>
    public string? Username { get; set; }

    public string From { get; set; } = string.Empty;

    /// <summary>Fixed recipient list — never templated (SMTP header-injection defence).</summary>
    public List<string> To { get; set; } = [];

    /// <summary><c>{field}</c> substitution applied; CR/LF stripped before use.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary><c>{field}</c> substitution applied.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>POST a JSON body to an HTTP(S) endpoint with retry/back-off (PHASE_07 item 3).</summary>
public sealed class HttpWebhookAction : RuleAction
{
    /// <summary>Absolute http/https URL. Private/link-local ranges are blocked unless <see cref="AllowPrivateNetwork"/>.</summary>
    public string Url { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    /// <summary>Extra headers; header values get <c>{field}</c> substitution, CR/LF stripped.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary><c>{field}</c> substitution applied. Sent as <c>application/json</c>.</summary>
    public string BodyTemplate { get; set; } = "{}";

    public int TimeoutSeconds { get; set; } = 10;

    public int MaxRetries { get; set; } = 3;

    /// <summary>Explicit opt-in to post to an RFC1918 / link-local host. Audited on save.</summary>
    public bool AllowPrivateNetwork { get; set; }
}

/// <summary>Run an allow-listed executable with an argument vector (PHASE_07 item 3).</summary>
public sealed class RunScriptAction : RuleAction
{
    /// <summary>Full path to the executable. Must resolve under a configured allow-list directory.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Argument vector — each item gets <c>{field}</c> substitution; passed with no shell.</summary>
    public List<string> Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Relay the event to another syslog collector (PHASE_07 item 3).</summary>
public sealed class ForwardSyslogAction : RuleAction
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 514;

    public ForwardTransport Transport { get; set; } = ForwardTransport.Udp;

    /// <summary>Optional reformat template; blank = forward <c>raw_message</c> verbatim.</summary>
    public string? Template { get; set; }
}

public enum ForwardTransport
{
    Udp,
    Tcp,
    Tls,
}

/// <summary>Append the event to a named, size/age-rotated file (PHASE_07 item 3).</summary>
public sealed class WriteToFileAction : RuleAction
{
    /// <summary>File name only (or a relative path); resolved under a configured base directory.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary><c>{field}</c> substitution applied; a newline is appended.</summary>
    public string LineTemplate { get; set; } = string.Empty;

    public long RotateAtBytes { get; set; } = 50L * 1024 * 1024;

    public int RotateAfterDays { get; set; } = 7;
}

/// <summary>Insert a row into an external SQL destination over ODBC (PHASE_07 item 3).</summary>
public sealed class WriteToOdbcAction : RuleAction
{
    /// <summary>ODBC connection string <em>without</em> credentials; the password is <see cref="RuleAction.SecretName"/>.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;

    /// <summary>Target column → <c>{field}</c> template. Column names are identifier-validated.</summary>
    public Dictionary<string, string> ColumnMap { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>Add an <c>event_fields</c> tag row to the event before it is stored (inline).</summary>
public sealed class AddTagAction : RuleAction
{
    /// <summary>Tag value; <c>{field}</c> substitution applied, sanitised to a safe token.</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>Also route the event to a stream (inline; adds to the Phase 6 routing result).</summary>
public sealed class RouteToStreamAction : RuleAction
{
    public long StreamId { get; set; }
}

/// <summary>Stop evaluating further rules for this message (inline). Storage is unaffected.</summary>
public sealed class SuppressAction : RuleAction
{ }

/// <summary>Raise a notification in the UI notification centre (PHASE_07 item 3).</summary>
public sealed class RaiseNotificationAction : RuleAction
{
    public NotificationLevel Level { get; set; } = NotificationLevel.Warning;

    /// <summary><c>{field}</c> substitution applied.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary><c>{field}</c> substitution applied.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>Severity of a UI notification.</summary>
public enum NotificationLevel
{
    Info,
    Warning,
    Critical,
}
