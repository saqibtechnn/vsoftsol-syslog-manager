using System.Text.Json;
using System.Text.Json.Serialization;
using VSoftSol.Syslog.Core.Reports;

namespace VSoftSol.Syslog.Data.Reports;

/// <summary>(De)serializes <see cref="ReportDeliveryConfig"/> for the <c>delivery_json</c>
/// column. Malformed input never throws — it degrades to "no delivery configured", the
/// same fail-safe precedent as <c>Dashboards.DashboardJson</c>.</summary>
internal static class ReportJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeDelivery(ReportDeliveryConfig delivery) => JsonSerializer.Serialize(delivery, Options);

    public static ReportDeliveryConfig DeserializeDelivery(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ReportDeliveryConfig.None;
        }

        try
        {
            return JsonSerializer.Deserialize<ReportDeliveryConfig>(json, Options) ?? ReportDeliveryConfig.None;
        }
        catch (JsonException)
        {
            return ReportDeliveryConfig.None;
        }
    }
}
