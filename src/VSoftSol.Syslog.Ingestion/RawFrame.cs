using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// One message as it came off the wire, before any parsing. The <see cref="Payload"/> is
/// the verbatim bytes (CLAUDE.md Constraint 4); a datagram or a single framed TCP message
/// maps to exactly one <see cref="RawFrame"/>.
/// </summary>
/// <remarks>
/// Instances are immutable and own their <see cref="Payload"/> array (listeners copy out
/// of pooled receive buffers before constructing one), so a frame is safe to hand to the
/// channel, the spill queue, and the repository without further copying.
/// </remarks>
public sealed class RawFrame
{
    public RawFrame(
        DateTimeOffset receivedUtc,
        string sourceIp,
        string listenerName,
        Protocol protocol,
        ReadOnlyMemory<byte> payload,
        bool truncated)
    {
        ReceivedUtc = receivedUtc;
        SourceIp = sourceIp ?? throw new ArgumentNullException(nameof(sourceIp));
        ListenerName = listenerName ?? throw new ArgumentNullException(nameof(listenerName));
        Protocol = protocol;
        Payload = payload;
        Truncated = truncated;
    }

    /// <summary>When the collector received the datagram / completed the frame.</summary>
    public DateTimeOffset ReceivedUtc { get; }

    /// <summary>Remote address the message arrived from. Unverified for UDP (spoofable).</summary>
    public string SourceIp { get; }

    /// <summary>Configured name of the listener that received it (for per-listener counters).</summary>
    public string ListenerName { get; }

    /// <summary>Transport the message arrived on.</summary>
    public Protocol Protocol { get; }

    /// <summary>Verbatim message bytes, already trimmed to <see cref="IngestionOptions.MaxMessageBytes"/>.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>True when the source sent more than <see cref="IngestionOptions.MaxMessageBytes"/>
    /// and the excess was cut. Recorded as an event field, never a reason to drop.</summary>
    public bool Truncated { get; }
}
