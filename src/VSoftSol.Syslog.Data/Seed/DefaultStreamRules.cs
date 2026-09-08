using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Data.Seed;

/// <summary>
/// The match rules for the seven default streams (PHASE_06 build item 7). Deliberately
/// plain substring / list tests — no regex — so they are cheap on the ingest path and an
/// operator can read and refine them in the visual builder. "All Messages" is the
/// catch-all and carries no rule.
/// </summary>
public static class DefaultStreamRules
{
    public sealed record Entry(string Name, bool IsCatchAll, ConditionGroup? Match);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("All Messages", IsCatchAll: true, Match: null),

        new("Security Events", false, AnyOf(
            Field("facility", ConditionOperator.InList, "4, 10"),
            Field("message", ConditionOperator.Contains, "denied"),
            Field("message", ConditionOperator.Contains, "violation"),
            Field("message", ConditionOperator.Contains, "%SEC"),
            Field("app", ConditionOperator.InList, "sshd, sudo"))),

        new("Interface Up/Down", false, AnyOf(
            Field("message", ConditionOperator.Contains, "changed state to"),
            Field("message", ConditionOperator.Contains, "line protocol on Interface"),
            Field("msg_id", ConditionOperator.Contains, "UPDOWN"),
            Field("message", ConditionOperator.Contains, "link down"),
            Field("message", ConditionOperator.Contains, "link up"))),

        new("Authentication Failures", false, AnyOf(
            Field("message", ConditionOperator.Contains, "authentication failure"),
            Field("message", ConditionOperator.Contains, "failed password"),
            Field("message", ConditionOperator.Contains, "login failed"),
            Field("message", ConditionOperator.Contains, "invalid user"),
            Field("message", ConditionOperator.Contains, "auth fail"),
            Field("message", ConditionOperator.Contains, "access denied"))),

        new("Configuration Changes", false, AnyOf(
            Field("message", ConditionOperator.Contains, "configured from"),
            Field("message", ConditionOperator.Contains, "config change"),
            Field("message", ConditionOperator.Contains, "%SYS-5-CONFIG"),
            Field("message", ConditionOperator.Contains, "running-config"),
            Field("message", ConditionOperator.Contains, "commit complete"))),

        new("Hardware/Environment", false, AnyOf(
            Field("message", ConditionOperator.Contains, "temperature"),
            Field("message", ConditionOperator.Contains, "power supply"),
            Field("message", ConditionOperator.Contains, "%ENVMON"),
            Field("message", ConditionOperator.Contains, "hardware failure"),
            Field("message", ConditionOperator.Contains, "fan failure"))),

        new("Parse Failures", false, AllOf(
            Field("parse_status", ConditionOperator.Equals, "raw"))),
    ];

    private static ConditionComparison Field(string name, ConditionOperator op, string value) =>
        new() { Field = name, Operator = op, Value = value };

    private static ConditionGroup AnyOf(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.Or, Children = [.. children] };

    private static ConditionGroup AllOf(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.And, Children = [.. children] };
}
