using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// The structured output of one parse attempt, before it is mapped onto the canonical
/// <see cref="Core.Events.SyslogEvent"/>. A parser returns <c>false</c> from
/// <see cref="ISyslogParser.TryParse"/> when the input is not its format; it never throws.
/// </summary>
public sealed record SyslogParseResult
{
    public required ParseStatus Status { get; init; }

    public Facility Facility { get; init; } = Facility.User;

    public Severity Severity { get; init; } = Severity.Notice;

    /// <summary>Timestamp parsed from the message, in UTC. Null if the message carried none.</summary>
    public DateTimeOffset? EventUtc { get; init; }

    /// <summary>
    /// True when the message carried a local timestamp with no timezone and no year, so the
    /// absolute instant is an inference (VENDOR_SUPPORT.md "Devices sending local time with
    /// no timezone"). Surfaced as the <c>timestamp_ambiguous</c> field.
    /// </summary>
    public bool TimestampAmbiguous { get; init; }

    public string? Hostname { get; init; }

    public string? AppName { get; init; }

    public string? ProcId { get; init; }

    public string? MsgId { get; init; }

    /// <summary>The human-readable message body (everything after the header).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>RFC 5424 structured-data elements serialised as JSON, or null.</summary>
    public string? StructuredDataJson { get; init; }

    /// <summary>A framing anomaly was detected (embedded CR/LF/NUL in a position that would
    /// let an attacker forge or split a record). The record is still stored as one event.</summary>
    public bool FramingAnomaly { get; init; }

    public static SyslogParseResult Raw(string message) => new()
    {
        Status = ParseStatus.Raw,
        Message = message,
    };
}
