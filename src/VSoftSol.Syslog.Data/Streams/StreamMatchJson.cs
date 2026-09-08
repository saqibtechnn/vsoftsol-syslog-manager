using System.Text.Json;
using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Data.Streams;

/// <summary>
/// Serialises the <see cref="ConditionNode"/> tree stored in <c>streams.match_json</c>
/// (and, from Phase 7, <c>rules.condition_json</c>). The model carries its own polymorphic
/// discriminator (<c>kind</c>), so this is just a pinned options object.
/// </summary>
public static class StreamMatchJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(ConditionNode? node) =>
        node is null ? string.Empty : JsonSerializer.Serialize(node, Options);

    public static ConditionNode? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConditionNode>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
