using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Turns a wire <see cref="RawFrame"/> into a canonical <see cref="SyslogEvent"/> with
/// <see cref="ParseStatus.Raw"/>. Phase 2 does no parsing (PHASE_02 "Do not build in this
/// phase: Parsing"): facility and severity get the RFC 5424 §6.2.1 no-PRI default
/// (<c>user.notice</c>, PRI 13) and <see cref="SyslogEvent.Message"/> stays empty. Phase 3
/// replaces this with the real parser chain.
/// </summary>
internal static class RawFrameMapper
{
    private static readonly IReadOnlyList<EventField> TruncatedField = [new EventField("truncated", "true")];

    public static SyslogEvent ToRawEvent(RawFrame frame) => new()
    {
        ReceivedUtc = frame.ReceivedUtc,
        SourceIp = frame.SourceIp,
        Facility = Facility.User,
        Severity = Severity.Notice,
        Protocol = frame.Protocol,
        ListenerId = 0, // events.listener_id link waits for the Phase 4 listener registry
        Message = string.Empty,
        RawMessage = frame.Payload,
        ParseStatus = ParseStatus.Raw,
        Fields = frame.Truncated ? TruncatedField : [],
    };
}
