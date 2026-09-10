using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.Data.Streams;

namespace VSoftSol.Syslog.Data.Alerts;

/// <summary>
/// Serialises the Phase 8 alert model to / from the <c>alert_definitions</c> columns. Reuses
/// the Phase 6 condition serializer for the filter and the Phase 7
/// <see cref="RuleJson"/> for the actions and the id lists — one JSON convention across
/// streams, rules, and alerts. A malformed column deserialises to a safe default, never an
/// exception.
/// </summary>
public static class AlertJson
{
    public static string EvalTypeToken(AlertEvaluationType type) => type switch
    {
        AlertEvaluationType.Threshold => "threshold",
        AlertEvaluationType.DistinctCount => "distinct_count",
        AlertEvaluationType.DeviceSilent => "device_silent",
        AlertEvaluationType.Absence => "absence",
        _ => "threshold",
    };

    public static AlertEvaluationType ParseEvalType(string token) => token switch
    {
        "distinct_count" => AlertEvaluationType.DistinctCount,
        "device_silent" => AlertEvaluationType.DeviceSilent,
        "absence" => AlertEvaluationType.Absence,
        _ => AlertEvaluationType.Threshold,
    };

    public static string SeverityToken(Core.Rules.NotificationLevel level) => level switch
    {
        Core.Rules.NotificationLevel.Info => "info",
        Core.Rules.NotificationLevel.Critical => "critical",
        _ => "warning",
    };

    public static Core.Rules.NotificationLevel ParseSeverity(string token) => token switch
    {
        "info" => Core.Rules.NotificationLevel.Info,
        "critical" => Core.Rules.NotificationLevel.Critical,
        _ => Core.Rules.NotificationLevel.Warning,
    };

    public static string SerializeActions(IReadOnlyList<Core.Rules.RuleAction> actions) =>
        RuleJson.SerializeActions(actions);

    public static List<Core.Rules.RuleAction> DeserializeActions(string? json) =>
        RuleJson.DeserializeActions(json);

    public static string SerializeAction(Core.Rules.RuleAction action) => RuleJson.SerializeAction(action);

    public static Core.Rules.RuleAction? DeserializeAction(string? json) => RuleJson.DeserializeAction(json);

    public static string? SerializeIds(IReadOnlyList<long> ids) => RuleJson.SerializeGroupIds(ids);

    public static List<long> DeserializeIds(string? json) => RuleJson.DeserializeGroupIds(json);

    public static string SerializeFilter(Core.Conditions.ConditionGroup? filter) => StreamMatchJson.Serialize(filter);

    public static Core.Conditions.ConditionGroup? DeserializeFilter(string? json) =>
        StreamMatchJson.Deserialize(json) as Core.Conditions.ConditionGroup;
}
