using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.UnitTests.Alerts;

internal static class AlertTestBuilders
{
    public static ConditionComparison Cmp(string field, ConditionOperator op, string value = "") =>
        new() { Field = field, Operator = op, Value = value };

    public static ConditionGroup All(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.And, Children = [.. children] };

    public static ConditionGroup Any(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.Or, Children = [.. children] };

    public static AlertDefinition Threshold(
        string name = "burst",
        int threshold = 5,
        string? groupBy = null,
        ConditionGroup? filter = null,
        int windowSeconds = 300,
        int intervalSeconds = 60) => new()
        {
            Name = name,
            Type = AlertEvaluationType.Threshold,
            Threshold = threshold,
            GroupByField = groupBy,
            Filter = filter,
            WindowSeconds = windowSeconds,
            IntervalSeconds = intervalSeconds,
            Actions = [new RaiseNotificationAction { Title = "fired" }],
        };

    public static AlertDefinition DistinctCount(string groupBy, int threshold = 10) => new()
    {
        Name = "spray",
        Type = AlertEvaluationType.DistinctCount,
        Threshold = threshold,
        GroupByField = groupBy,
        WindowSeconds = 600,
        IntervalSeconds = 60,
    };

    public static AlertDefinition DeviceSilent(int windowSeconds = 900, params long[] groups) => new()
    {
        Name = "dead switch",
        Type = AlertEvaluationType.DeviceSilent,
        WindowSeconds = windowSeconds,
        IntervalSeconds = 300,
        DeviceGroupIds = [.. groups],
        Actions = [new RaiseNotificationAction { Title = "silent" }],
    };

    public static AlertDefinition Absence(ConditionGroup filter, int windowSeconds = 86400) => new()
    {
        Name = "no backup",
        Type = AlertEvaluationType.Absence,
        Filter = filter,
        WindowSeconds = windowSeconds,
        IntervalSeconds = 3600,
    };
}
