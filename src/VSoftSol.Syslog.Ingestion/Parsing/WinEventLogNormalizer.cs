using System.Text.Json;
using System.Text.Json.Serialization;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.WinEventLog;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// Turns a forwarded Windows Event Log JSON body (PHASE_11 item 3) into the same
/// <see cref="SyslogParseResult"/> + <see cref="EventField"/> shape every other source
/// produces. Reported as <see cref="ParseStatus.Raw"/> for the same reason as
/// <see cref="SnmpTrapNormalizer"/> — <c>protocol = wineventlog</c> is the disambiguator.
/// Malformed JSON never throws past this boundary (Constraint 4: the raw bytes are still
/// stored and searchable via the raw fallback).
/// </summary>
public static class WinEventLogNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static (SyslogParseResult Result, IReadOnlyList<EventField> Fields) Normalize(
        ReadOnlySpan<byte> payload, int maxFields, int maxValueLength)
    {
        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return (SyslogParseResult.Raw("(unparseable Windows Event Log body — not valid JSON)"), []);
        }

        if (wire is null || string.IsNullOrWhiteSpace(wire.Computer) || string.IsNullOrWhiteSpace(wire.Channel))
        {
            return (SyslogParseResult.Raw("(Windows Event Log body missing required fields: computer, channel)"), []);
        }

        Severity severity = WinEventLogMapper.SeverityFor(wire.Level);
        Facility facility = WinEventLogMapper.FacilityFor(wire.Channel);
        DateTimeOffset? eventUtc = wire.TimeCreated?.ToUniversalTime();

        var result = new SyslogParseResult
        {
            Status = ParseStatus.Raw,
            Facility = facility,
            Severity = severity,
            EventUtc = eventUtc,
            Hostname = wire.Computer,
            AppName = string.IsNullOrWhiteSpace(wire.ProviderName) ? "wineventlog" : wire.ProviderName,
            MsgId = wire.EventId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Message = wire.Message ?? string.Empty,
        };

        var fields = new List<EventField>();

        void Add(string name, string? value)
        {
            if (string.IsNullOrEmpty(value) || fields.Count >= maxFields)
            {
                return;
            }

            fields.Add(new EventField(name, value.Length > maxValueLength ? value[..maxValueLength] : value));
        }

        Add("wel_channel", wire.Channel);
        Add("wel_provider", wire.ProviderName);
        Add("wel_event_id", wire.EventId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("wel_level", wire.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (wire.Data is not null)
        {
            foreach ((string key, string value) in wire.Data)
            {
                if (fields.Count >= maxFields)
                {
                    break;
                }

                Add($"wel_data.{key}", value);
            }
        }

        return (result, fields);
    }

    private sealed record Wire(
        [property: JsonPropertyName("computer")] string? Computer,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("provider")] string? ProviderName,
        [property: JsonPropertyName("eventId")] int? EventId,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("timeCreated")] DateTimeOffset? TimeCreated,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("data")] Dictionary<string, string>? Data);
}
