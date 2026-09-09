using System.Text.Json;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Streams;

namespace VSoftSol.Syslog.Data.Rules;

/// <summary>
/// Serialises the Phase 7 rule model to/from the <c>rules</c> columns. Reuses the pinned
/// camelCase options from <see cref="StreamMatchJson"/> so the polymorphic <c>kind</c>
/// discriminators on <see cref="RuleAction"/> round-trip. A malformed column deserialises
/// to a safe default (no actions / no policy), never an exception.
/// </summary>
public static class RuleJson
{
    private static JsonSerializerOptions Options => StreamMatchJson.Options;

    public static string SerializeActions(IReadOnlyList<RuleAction> actions) =>
        JsonSerializer.Serialize(actions, Options);

    public static List<RuleAction> DeserializeActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RuleAction>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeAction(RuleAction action) => JsonSerializer.Serialize(action, Options);

    public static RuleAction? DeserializeAction(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RuleAction>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? SerializeWindow(TimeOfDayWindow? window) =>
        window is null ? null : JsonSerializer.Serialize(window, Options);

    public static TimeOfDayWindow? DeserializeWindow(string? json) => Deserialize<TimeOfDayWindow>(json);

    public static string? SerializeEscalation(EscalationPolicy? policy) =>
        policy is null ? null : JsonSerializer.Serialize(policy, Options);

    public static EscalationPolicy? DeserializeEscalation(string? json) => Deserialize<EscalationPolicy>(json);

    public static string? SerializeGroupIds(IReadOnlyList<long> ids) =>
        ids.Count == 0 ? null : JsonSerializer.Serialize(ids, Options);

    public static List<long> DeserializeGroupIds(string? json) => Deserialize<List<long>>(json) ?? [];

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
