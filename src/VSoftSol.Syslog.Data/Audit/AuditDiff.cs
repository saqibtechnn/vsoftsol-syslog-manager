using System.Text.Json;
using System.Text.Json.Nodes;

namespace VSoftSol.Syslog.Data.Audit;

/// <summary>
/// Produces the redacted before/after JSON snapshots stored on a <see cref="AuditEntry"/>
/// for a configuration change. Property names that look like secrets (or are named
/// explicitly) are replaced with a fixed marker so a plaintext secret never lands in the
/// audit trail (SECURITY_STANDARDS.md §5.5).
/// </summary>
public static class AuditDiff
{
    public const string RedactedMarker = "***REDACTED***";

    private static readonly string[] SecretNameFragments =
        ["password", "passwd", "secret", "token", "apikey", "api_key", "credential", "connectionstring", "connection_string", "privatekey", "private_key"];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>Serialises <paramref name="value"/> to a single-line JSON string with secret-valued properties redacted.</summary>
    public static string? Snapshot(object? value, IEnumerable<string>? extraRedactKeys = null)
    {
        if (value is null)
        {
            return null;
        }

        JsonNode? node = JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);
        if (node is null)
        {
            return null;
        }

        var explicitKeys = new HashSet<string>(extraRedactKeys ?? [], StringComparer.OrdinalIgnoreCase);
        Redact(node, explicitKeys);
        return node.ToJsonString(SerializerOptions);
    }

    /// <summary>The set of top-level property names whose values differ between two snapshots.</summary>
    public static IReadOnlyList<string> ChangedKeys(string? beforeJson, string? afterJson)
    {
        JsonObject? before = Parse(beforeJson);
        JsonObject? after = Parse(afterJson);
        if (before is null && after is null)
        {
            return [];
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string key in Union(before, after))
        {
            string? b = before?[key]?.ToJsonString();
            string? a = after?[key]?.ToJsonString();
            if (!string.Equals(b, a, StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        return [.. keys];
    }

    private static void Redact(JsonNode node, HashSet<string> explicitKeys)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string name in obj.Select(kvp => kvp.Key).ToList())
                {
                    if (IsSecretName(name) || explicitKeys.Contains(name))
                    {
                        obj[name] = obj[name] is null ? null : RedactedMarker;
                    }
                    else if (obj[name] is { } child)
                    {
                        Redact(child, explicitKeys);
                    }
                }

                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item is not null)
                    {
                        Redact(item, explicitKeys);
                    }
                }

                break;
        }
    }

    private static bool IsSecretName(string name)
    {
        string normalised = name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        foreach (string fragment in SecretNameFragments)
        {
            if (normalised.Contains(fragment.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Union(JsonObject? a, JsonObject? b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject? obj in new[] { a, b })
        {
            if (obj is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> kvp in obj)
            {
                if (seen.Add(kvp.Key))
                {
                    yield return kvp.Key;
                }
            }
        }
    }
}
