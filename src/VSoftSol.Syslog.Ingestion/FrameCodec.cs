using System.Buffers.Binary;
using System.Text;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Length-prefixed binary encoding for <see cref="RawFrame"/> in the disk spill queue.
///
/// <para>Layout (little-endian):
/// <c>[uint32 bodyLength][byte version][byte protocol][byte flags][int64 receivedUtcTicks]
/// [uint16 ipLen][ip][uint16 listenerLen][listener][uint32 payloadLen][payload]</c>.</para>
///
/// <para>The leading <c>bodyLength</c> lets a reader detect a torn tail from a hard kill
/// mid-write: if fewer than <c>4 + bodyLength</c> bytes remain, the record is incomplete
/// and recovery stops there with everything before it intact.</para>
/// </summary>
internal static class FrameCodec
{
    private const byte Version = 1;
    private const byte FlagTruncated = 0b0000_0001;

    /// <summary>Upper bound on the encoded size of <paramref name="frame"/>.</summary>
    public static int MaxEncodedSize(RawFrame frame) =>
        4 + 1 + 1 + 1 + 8
        + 2 + Encoding.UTF8.GetMaxByteCount(frame.SourceIp.Length)
        + 2 + Encoding.UTF8.GetMaxByteCount(frame.ListenerName.Length)
        + 4 + frame.Payload.Length;

    /// <summary>Encodes <paramref name="frame"/> into <paramref name="destination"/>, returning
    /// the number of bytes written. <paramref name="destination"/> must be at least
    /// <see cref="MaxEncodedSize"/> long.</summary>
    public static int Encode(RawFrame frame, Span<byte> destination)
    {
        Span<byte> ip = stackalloc byte[Encoding.UTF8.GetByteCount(frame.SourceIp)];
        Encoding.UTF8.GetBytes(frame.SourceIp, ip);
        Span<byte> listener = stackalloc byte[Encoding.UTF8.GetByteCount(frame.ListenerName)];
        Encoding.UTF8.GetBytes(frame.ListenerName, listener);
        ReadOnlySpan<byte> payload = frame.Payload.Span;

        int body = 1 + 1 + 1 + 8 + 2 + ip.Length + 2 + listener.Length + 4 + payload.Length;
        int pos = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(destination[pos..], (uint)body);
        pos += 4;
        destination[pos++] = Version;
        destination[pos++] = (byte)frame.Protocol;
        destination[pos++] = frame.Truncated ? FlagTruncated : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(destination[pos..], frame.ReceivedUtc.UtcTicks);
        pos += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[pos..], (ushort)ip.Length);
        pos += 2;
        ip.CopyTo(destination[pos..]);
        pos += ip.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[pos..], (ushort)listener.Length);
        pos += 2;
        listener.CopyTo(destination[pos..]);
        pos += listener.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[pos..], (uint)payload.Length);
        pos += 4;
        payload.CopyTo(destination[pos..]);
        pos += payload.Length;

        return pos;
    }

    /// <summary>
    /// Tries to decode one record from the front of <paramref name="source"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> with <paramref name="frame"/> set and <paramref name="consumed"/> = the
    /// record's total on-disk size. <c>false</c> when the buffer does not yet hold a whole
    /// record (torn tail) — <paramref name="consumed"/> is 0 and recovery should stop.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The record's framing is internally inconsistent (corruption, not just truncation).
    /// </exception>
    public static bool TryDecode(ReadOnlySpan<byte> source, out RawFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        if (source.Length < 4)
        {
            return false;
        }

        uint body = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (body is 0 or > (16 * 1024 * 1024) + 4096)
        {
            throw new InvalidDataException($"Spill record length {body} is out of range.");
        }

        long total = 4L + body;
        if (source.Length < total)
        {
            return false;
        }

        ReadOnlySpan<byte> b = source.Slice(4, (int)body);
        int pos = 0;

        byte version = b[pos++];
        if (version != Version)
        {
            throw new InvalidDataException($"Unknown spill record version {version}.");
        }

        byte protocol = b[pos++];
        byte flags = b[pos++];
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(b[pos..]);
        pos += 8;

        if (pos + 2 > b.Length)
        {
            throw new InvalidDataException("Spill record truncated at source-ip length.");
        }

        int ipLen = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
        pos += 2;
        if (pos + ipLen + 2 > b.Length)
        {
            throw new InvalidDataException("Spill record truncated at source-ip.");
        }

        string ip = Encoding.UTF8.GetString(b.Slice(pos, ipLen));
        pos += ipLen;

        int listenerLen = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
        pos += 2;
        if (pos + listenerLen + 4 > b.Length)
        {
            throw new InvalidDataException("Spill record truncated at listener name.");
        }

        string listener = Encoding.UTF8.GetString(b.Slice(pos, listenerLen));
        pos += listenerLen;

        long payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
        pos += 4;
        if (pos + payloadLen != b.Length)
        {
            throw new InvalidDataException("Spill record payload length does not match the record.");
        }

        var payload = b.Slice(pos, (int)payloadLen).ToArray();

        frame = new RawFrame(
            new DateTimeOffset(ticks, TimeSpan.Zero),
            ip,
            listener,
            (Protocol)protocol,
            payload,
            (flags & FlagTruncated) != 0);
        consumed = (int)total;
        return true;
    }
}
