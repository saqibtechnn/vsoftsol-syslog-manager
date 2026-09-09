using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Data.Seed;

/// <summary>
/// The starter library of pre-built rule templates (PHASE_07 UX gate — "a starter library
/// of 8-10 pre-built rule templates users can clone rather than author from scratch").
/// These are <b>not</b> seeded into the <c>rules</c> table; the UI clones one into a new,
/// fully editable rule. Templates that need a destination (SMTP host, webhook URL, script
/// path) ship with a blank placeholder so the operator fills it in — a clone is disabled
/// until it validates.
/// </summary>
public static class DefaultRuleTemplates
{
    public sealed record Template(string Category, string Summary, RuleDefinition Rule);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("Alerting", "Email the on-call address whenever any message is severity Critical or worse.",
            Def("Email on critical", 10,
                AllOf(Field("severity", ConditionOperator.LessThan, "3")),
                new SendEmailAction
                {
                    Host = string.Empty,
                    From = string.Empty,
                    To = [],
                    Subject = "[{severity}] {hostname}: {message}",
                    Body = "Device: {hostname} ({source_ip})\nApp: {app}\nMessage: {message}\nReceived: {received}",
                    Throttle = new ActionThrottle(20, 3600, 0),
                })),

        new("Alerting", "Raise a UI notification for every authentication failure.",
            Def("Notify on auth failure", 20,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "authentication failure"),
                    Field("message", ConditionOperator.Contains, "failed password"),
                    Field("message", ConditionOperator.Contains, "invalid user")),
                new RaiseNotificationAction
                {
                    Level = NotificationLevel.Warning,
                    Title = "Auth failure on {hostname}",
                    Body = "{message}",
                    Throttle = new ActionThrottle(0, 0, 60),
                })),

        new("Escalation", "Notify on repeated hardware/environment alarms; escalate to email on the 5th in 10 minutes.",
            Escalated("Escalate hardware alarms", 30,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "temperature"),
                    Field("message", ConditionOperator.Contains, "power supply"),
                    Field("message", ConditionOperator.Contains, "fan failure")),
                normal: new RaiseNotificationAction { Level = NotificationLevel.Warning, Title = "Hardware alarm: {hostname}", Body = "{message}" },
                threshold: 5, windowSeconds: 600,
                escalation: new SendEmailAction
                {
                    Host = string.Empty, From = string.Empty, To = [],
                    Subject = "REPEATED hardware alarm on {hostname}",
                    Body = "5+ hardware alarms in 10 minutes from {hostname} ({source_ip}).\nLatest: {message}",
                })),

        new("Forwarding", "Forward every firewall 'deny' to an upstream SIEM by syslog.",
            Def("Forward firewall denies to SIEM", 40,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "%ASA-"),
                    Field("message", ConditionOperator.Contains, " Deny "),
                    Field("message", ConditionOperator.Contains, "denied")),
                new ForwardSyslogAction { Host = string.Empty, Port = 514, Transport = ForwardTransport.Udp })),

        new("Enrichment", "Tag configuration-change events and route them to the Configuration Changes stream.",
            Def("Tag & route config changes", 50,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "configured from"),
                    Field("message", ConditionOperator.Contains, "%SYS-5-CONFIG"),
                    Field("message", ConditionOperator.Contains, "commit complete")),
                new AddTagAction { Tag = "config-change" })),

        new("Noise reduction", "Suppress further rule processing for routine keepalive / heartbeat chatter.",
            Def("Suppress keepalive noise", 5,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "keepalive"),
                    Field("message", ConditionOperator.Contains, "heartbeat"),
                    Field("message", ConditionOperator.Contains, "DHCPACK")),
                new SuppressAction())),

        new("Integration", "POST a JSON summary of any interface-down event to a chat webhook.",
            Def("Webhook on interface down", 60,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "changed state to down"),
                    Field("message", ConditionOperator.Contains, "link down")),
                new HttpWebhookAction
                {
                    Url = string.Empty,
                    BodyTemplate = "{{\"text\":\"Interface down on {hostname}: {message}\"}}",
                    TimeoutSeconds = 10,
                    MaxRetries = 3,
                    Throttle = new ActionThrottle(60, 60, 0),
                })),

        new("Archival", "Append every security-facility message to a rotating on-disk file.",
            Def("Archive security events to file", 70,
                AllOf(Field("facility", ConditionOperator.InList, "4, 10")),
                new WriteToFileAction
                {
                    FileName = "security-events.log",
                    LineTemplate = "{received}\t{hostname}\t{severity}\t{message}",
                    RotateAtBytes = 50L * 1024 * 1024,
                    RotateAfterDays = 30,
                })),

        new("Remediation", "Run an operator remediation script when a specific device reports a link failure (fill in the script path).",
            Def("Run remediation script on link failure", 80,
                AllOf(Field("message", ConditionOperator.Contains, "line protocol on Interface")),
                new RunScriptAction
                {
                    ExecutablePath = string.Empty,
                    Arguments = ["--host", "{hostname}", "--message", "{message}"],
                    TimeoutSeconds = 60,
                    Throttle = new ActionThrottle(0, 0, 300),
                })),

        new("Integration", "Insert authentication events into an external SQL table over ODBC (fill in the DSN).",
            Def("Mirror auth events to a SQL table", 90,
                AnyOf(
                    Field("message", ConditionOperator.Contains, "authentication"),
                    Field("message", ConditionOperator.Contains, "login")),
                new WriteToOdbcAction
                {
                    ConnectionString = string.Empty,
                    TableName = "syslog_auth",
                    ColumnMap = { ["received_utc"] = "{received}", ["host"] = "{hostname}", ["msg"] = "{message}" },
                })),
    ];

    private static RuleDefinition Def(string name, int priority, ConditionGroup filter, params RuleAction[] actions) => new()
    {
        Name = name,
        Priority = priority,
        Enabled = false, // a clone is disabled until the operator completes and enables it
        Filter = filter,
        Actions = [.. actions],
    };

    private static RuleDefinition Escalated(
        string name, int priority, ConditionGroup filter, RuleAction normal,
        int threshold, int windowSeconds, RuleAction escalation)
    {
        RuleDefinition rule = Def(name, priority, filter, normal);
        rule.Escalation = new EscalationPolicy
        {
            Threshold = threshold,
            WindowSeconds = windowSeconds,
            EscalationActions = [escalation],
        };
        return rule;
    }

    private static ConditionComparison Field(string name, ConditionOperator op, string value) =>
        new() { Field = name, Operator = op, Value = value };

    private static ConditionGroup AnyOf(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.Or, Children = [.. children] };

    private static ConditionGroup AllOf(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.And, Children = [.. children] };
}
