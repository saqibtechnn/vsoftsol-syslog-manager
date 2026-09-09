using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.UnitTests.Rules;

internal static class RuleTestBuilders
{
    public static ConditionComparison Cmp(string field, ConditionOperator op, string value = "") =>
        new() { Field = field, Operator = op, Value = value };

    public static ConditionGroup All(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.And, Children = [.. children] };

    public static ConditionGroup Any(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.Or, Children = [.. children] };

    /// <summary>A rule with default priority 100, a filter, and one or more actions.</summary>
    public static RuleDefinition Rule(string name, ConditionGroup? filter, params RuleAction[] actions) =>
        Rule(name, 100, filter, actions);

    /// <summary>A rule with an explicit priority.</summary>
    public static RuleDefinition Rule(string name, int priority, ConditionGroup? filter, params RuleAction[] actions) =>
        new()
        {
            Name = name,
            Priority = priority,
            Enabled = true,
            Filter = filter,
            Actions = actions.Length == 0 ? [new RaiseNotificationAction { Title = "hit" }] : [.. actions],
        };

    public static RaiseNotificationAction Notify(string title = "hit", ActionThrottle? throttle = null) =>
        new() { Title = title, Throttle = throttle ?? ActionThrottle.None };

    public static SuppressAction Suppress() => new();

    public static AddTagAction Tag(string value) => new() { Tag = value };

    public static RouteToStreamAction Route(long streamId) => new() { StreamId = streamId };
}
