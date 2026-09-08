using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.Events;

/// <summary>
/// The canonical event record (BUILD_PLAN.md "Canonical event schema"). This shape is
/// fixed; it does not change without an ADR. Every phase from 3 onward depends on it.
/// </summary>
/// <remarks>
/// <see cref="RawMessage"/> is ALWAYS populated, including for messages that fail to
/// parse (CLAUDE.md Constraint 4). Timestamps are UTC; conversion to local happens only
/// at the UI edge.
/// </remarks>
public sealed class SyslogEvent
{
    /// <summary><c>event_id</c> — assigned by the repository on append. 0 before persistence.</summary>
    public long EventId { get; init; }

    /// <summary><c>received_utc</c> — when the collector received the datagram/frame.</summary>
    public required DateTimeOffset ReceivedUtc { get; init; }

    /// <summary><c>event_utc</c> — timestamp parsed from the message; null if absent or unparseable.</summary>
    public DateTimeOffset? EventUtc { get; init; }

    /// <summary><c>source_ip</c> — remote address the message arrived from (may be spoofed for UDP).</summary>
    public required string SourceIp { get; init; }

    /// <summary><c>hostname</c> — host field from the message, or null.</summary>
    public string? Hostname { get; init; }

    /// <summary><c>app_name</c> — application/tag field, or null.</summary>
    public string? AppName { get; init; }

    /// <summary><c>proc_id</c> — process id field, or null.</summary>
    public string? ProcId { get; init; }

    /// <summary><c>msg_id</c> — RFC 5424 MSGID, or null.</summary>
    public string? MsgId { get; init; }

    /// <summary><c>facility</c> — 0-23.</summary>
    public Facility Facility { get; init; }

    /// <summary><c>severity</c> — 0-7.</summary>
    public Severity Severity { get; init; }

    /// <summary><c>protocol</c> — transport the message was received on.</summary>
    public required Protocol Protocol { get; init; }

    /// <summary><c>listener_id</c> — FK to the listener that received it.</summary>
    public long ListenerId { get; init; }

    /// <summary><c>message</c> — parsed message body. Empty when <see cref="ParseStatus"/> is Raw.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary><c>raw_message</c> — the original bytes, verbatim. Never null, never empty for a real message.</summary>
    public required ReadOnlyMemory<byte> RawMessage { get; init; }

    /// <summary><c>parse_status</c> — which stage of the fallback chain produced this record.</summary>
    public required ParseStatus ParseStatus { get; init; }

    /// <summary><c>occurrence_count</c> — de-duplication count; 1 for a distinct message.</summary>
    public int OccurrenceCount { get; init; } = 1;

    /// <summary><c>structured_data_json</c> — RFC 5424 SD elements as JSON, or null.</summary>
    public string? StructuredDataJson { get; init; }

    /// <summary><c>device_id</c> — FK to the registered device, or null if not yet matched.</summary>
    public long? DeviceId { get; init; }

    /// <summary><c>vendor</c> — vendor key resolved by the parser pack, or null.</summary>
    public string? Vendor { get; init; }

    /// <summary>
    /// Extracted custom fields (<c>event_fields</c> table). Vendor patterns add rows here,
    /// so a new pattern never needs a schema migration.
    /// </summary>
    public IReadOnlyList<EventField> Fields { get; init; } = [];

    /// <summary>
    /// The stream ids this event was routed to at ingest (PHASE_06; ADR 0014). This is a
    /// transient routing result, not a stored column — the repository writes it to the
    /// <c>event_streams</c> join table. Empty on a freshly parsed event.
    /// </summary>
    public IReadOnlyList<long> StreamIds { get; init; } = [];

    /// <summary>Returns a copy with the ingest-time device resolution and stream routing applied.</summary>
    public SyslogEvent WithRouting(long? deviceId, IReadOnlyList<long> streamIds)
    {
        ArgumentNullException.ThrowIfNull(streamIds);
        return new SyslogEvent
        {
            EventId = EventId,
            ReceivedUtc = ReceivedUtc,
            EventUtc = EventUtc,
            SourceIp = SourceIp,
            Hostname = Hostname,
            AppName = AppName,
            ProcId = ProcId,
            MsgId = MsgId,
            Facility = Facility,
            Severity = Severity,
            Protocol = Protocol,
            ListenerId = ListenerId,
            Message = Message,
            RawMessage = RawMessage,
            ParseStatus = ParseStatus,
            OccurrenceCount = OccurrenceCount,
            StructuredDataJson = StructuredDataJson,
            DeviceId = deviceId ?? DeviceId,
            Vendor = Vendor,
            Fields = Fields,
            StreamIds = streamIds,
        };
    }
}
