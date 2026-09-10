using System.Text.Json;
using System.Text.Json.Serialization;
using VSoftSol.Syslog.Core.Dashboards;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// Serialises the widget list and grid layout to/from the <c>dashboards</c> JSON columns.
/// Enums are written as camelCase names (stable across releases); an unknown value or a
/// malformed column deserialises to a safe empty list, never an exception — a corrupt row
/// must not take a page down.
/// </summary>
public static class DashboardJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static string SerializeWidgets(IReadOnlyList<WidgetDefinition> widgets) =>
        JsonSerializer.Serialize(widgets, Options);

    public static List<WidgetDefinition> DeserializeWidgets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            List<WidgetDefinition>? list = JsonSerializer.Deserialize<List<WidgetDefinition>>(json, Options);
            return list is null ? [] : [.. list.Where(w => w is not null && !string.IsNullOrWhiteSpace(w.Id))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeLayout(IReadOnlyList<WidgetLayout> layout) =>
        JsonSerializer.Serialize(layout, Options);

    public static List<WidgetLayout> DeserializeLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            List<WidgetLayout>? list = JsonSerializer.Deserialize<List<WidgetLayout>>(json, Options);
            return list is null ? [] : [.. list.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.WidgetId))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
