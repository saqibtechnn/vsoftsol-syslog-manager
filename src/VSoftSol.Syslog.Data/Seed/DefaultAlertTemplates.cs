using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Data.Seed;

/// <summary>
/// The starter library of pre-built alert templates (PHASE_08 UX gate — "ship 6-8 alert
/// templates"). Not seeded into <c>alert_definitions</c>; the UI clones one into a new,
/// fully editable alert that starts disabled until the operator completes and enables it.
/// </summary>
public static class DefaultAlertTemplates
{
    public sealed record Template(string Category, string Summary, AlertDefinition Alert);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("Security", "Fire when one source IP racks up repeated authentication failures in two minutes.",
            Threshold("Repeated login failures from one source", NotificationLevel.Warning,
                window: 120, interval: 60, groupBy: "source_ip", threshold: 5,
                filter: AnyOf(
                    Field("message", ConditionOperator.Contains, "authentication failure"),
                    Field("message", ConditionOperator.Contains, "failed password"),
                    Field("message", ConditionOperator.Contains, "invalid user")),
                action: Notify("Login failures from {hostname}", "5+ auth failures in 2 minutes from one source."))),

        new("Security", "Fire when failed logins are seen from an unusual number of distinct source IPs (a spray attack).",
            Distinct("Password-spray — many distinct sources", NotificationLevel.Critical,
                window: 300, interval: 120, groupBy: "source_ip", threshold: 20,
                filter: AnyOf(
                    Field("message", ConditionOperator.Contains, "authentication failure"),
                    Field("message", ConditionOperator.Contains, "failed password")))),

        new("Availability", "Fire when a monitored device stops sending logs — a dead switch, a broken syslog config, or a failed path.",
            DeviceSilent("A monitored device went silent", NotificationLevel.Critical, window: 900, interval: 300,
                action: Notify("Device silent: {hostname}", "No logs received for longer than its heartbeat threshold."))),

        new("Availability", "Fire when the nightly backup produced no log line in the last 24 hours.",
            Absence("No nightly backup log", NotificationLevel.Warning, window: 86_400, interval: 3600,
                filter: AnyOf(
                    Field("app", ConditionOperator.Equals, "backup"),
                    Field("message", ConditionOperator.Contains, "backup completed")))),

        new("Networking", "Fire when one interface flaps (changes state) many times in five minutes.",
            Threshold("Interface flapping", NotificationLevel.Warning,
                window: 300, interval: 60, groupBy: "hostname", threshold: 10,
                filter: AnyOf(
                    Field("message", ConditionOperator.Contains, "changed state to"),
                    Field("message", ConditionOperator.Contains, "line protocol on Interface")),
                action: Notify("Interface flapping on {hostname}", "10+ interface state changes in 5 minutes."))),

        new("Change control", "Fire when a device reports several configuration changes in ten minutes.",
            Threshold("Configuration-change burst", NotificationLevel.Warning,
                window: 600, interval: 120, groupBy: "hostname", threshold: 3,
                filter: AnyOf(
                    Field("message", ConditionOperator.Contains, "configured from"),
                    Field("message", ConditionOperator.Contains, "commit complete"),
                    Field("message", ConditionOperator.Contains, "%SYS-5-CONFIG")),
                action: Notify("Config changes on {hostname}", "3+ configuration changes in 10 minutes."))),

        new("Security", "Fire when a firewall logs a flood of denies from one source in a minute.",
            Threshold("Firewall deny storm from one source", NotificationLevel.Warning,
                window: 60, interval: 60, groupBy: "source_ip", threshold: 100,
                filter: AnyOf(
                    Field("message", ConditionOperator.Contains, " Deny "),
                    Field("message", ConditionOperator.Contains, "denied")),
                action: Notify("Deny storm on {hostname}", "100+ firewall denies in a minute from one source."))),

        new("Hardware", "Fire immediately on any critical-or-worse message, grouped by device.",
            Threshold("Critical hardware / environment alarm", NotificationLevel.Critical,
                window: 300, interval: 60, groupBy: "hostname", threshold: 1,
                filter: AllOf(Field("severity", ConditionOperator.LessThan, "3")),
                action: Notify("Critical on {hostname}", "A critical-or-worse message was received."))),
    ];

    private static AlertDefinition Base(string name, NotificationLevel severity, AlertEvaluationType type,
        int window, int interval, ConditionGroup? filter, RuleAction? action) => new()
        {
            Name = name,
            Severity = severity,
            Type = type,
            Enabled = false,
            WindowSeconds = window,
            IntervalSeconds = interval,
            Filter = filter,
            Actions = action is null ? [] : [action],
        };

    private static AlertDefinition Threshold(string name, NotificationLevel severity, int window, int interval,
        string? groupBy, int threshold, ConditionGroup filter, RuleAction? action = null)
    {
        AlertDefinition a = Base(name, severity, AlertEvaluationType.Threshold, window, interval, filter, action);
        a.GroupByField = groupBy;
        a.Threshold = threshold;
        return a;
    }

    private static AlertDefinition Distinct(string name, NotificationLevel severity, int window, int interval,
        string groupBy, int threshold, ConditionGroup filter)
    {
        AlertDefinition a = Base(name, severity, AlertEvaluationType.DistinctCount, window, interval, filter, null);
        a.GroupByField = groupBy;
        a.Threshold = threshold;
        return a;
    }

    private static AlertDefinition DeviceSilent(string name, NotificationLevel severity, int window, int interval, RuleAction action)
    {
        AlertDefinition a = Base(name, severity, AlertEvaluationType.DeviceSilent, window, interval, null, action);
        a.AutoResolve = true;
        return a;
    }

    private static AlertDefinition Absence(string name, NotificationLevel severity, int window, int interval, ConditionGroup filter) =>
        Base(name, severity, AlertEvaluationType.Absence, window, interval, filter, null);

    private static RaiseNotificationAction Notify(string title, string body) =>
        new() { Level = NotificationLevel.Warning, Title = title, Body = body };

    private static ConditionComparison Field(string name, ConditionOperator op, string value) =>
        new() { Field = name, Operator = op, Value = value };

    private static ConditionGroup AnyOf(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.Or, Children = [.. children] };

    private static ConditionGroup AllOf(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.And, Children = [.. children] };
}
