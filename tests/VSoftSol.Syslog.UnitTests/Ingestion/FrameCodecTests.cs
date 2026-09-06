using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Ingestion;

public sealed class FrameCodecTests
{
    private static RawFrame Sample(byte[]? payload = null, bool truncated = false) => new(
        new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
        "198.51.100.9",
        "udp:0.0.0.0:514",
        Protocol.Udp,
        payload ?? Encoding.UTF8.GetBytes("<13>hello world"),
        truncated);

    [Fact]
    public void Encode_ThenTryDecode_RoundTripsEveryField()
    {
        RawFrame original = Sample(truncated: true);
        byte[] buffer = new byte[FrameCodec.MaxEncodedSize(original)];
        int written = FrameCodec.Encode(original, buffer);

        bool ok = FrameCodec.TryDecode(buffer.AsSpan(0, written), out RawFrame? decoded, out int consumed);

        ok.Should().BeTrue();
        consumed.Should().Be(written);
        decoded!.ReceivedUtc.Should().Be(original.ReceivedUtc);
        decoded.SourceIp.Should().Be(original.SourceIp);
        decoded.ListenerName.Should().Be(original.ListenerName);
        decoded.Protocol.Should().Be(Protocol.Udp);
        decoded.Truncated.Should().BeTrue();
        decoded.Payload.ToArray().Should().Equal(original.Payload.ToArray());
    }

    [Fact]
    public void TryDecode_WithAShortTail_ReturnsFalseAndConsumesNothing()
    {
        RawFrame original = Sample();
        byte[] buffer = new byte[FrameCodec.MaxEncodedSize(original)];
        int written = FrameCodec.Encode(original, buffer);

        FrameCodec.TryDecode(buffer.AsSpan(0, written - 3), out RawFrame? decoded, out int consumed)
            .Should().BeFalse();
        decoded.Should().BeNull();
        consumed.Should().Be(0);
    }

    [Fact]
    public void TryDecode_TwoConcatenatedRecords_ReadsBothInSequence()
    {
        RawFrame a = Sample(Encoding.UTF8.GetBytes("first"));
        RawFrame b = Sample(Encoding.UTF8.GetBytes("second"));
        byte[] buf = new byte[FrameCodec.MaxEncodedSize(a) + FrameCodec.MaxEncodedSize(b)];
        int n = FrameCodec.Encode(a, buf);
        n += FrameCodec.Encode(b, buf.AsSpan(n));

        FrameCodec.TryDecode(buf.AsSpan(0, n), out RawFrame? first, out int c1).Should().BeTrue();
        FrameCodec.TryDecode(buf.AsSpan(c1, n - c1), out RawFrame? second, out _).Should().BeTrue();

        Encoding.UTF8.GetString(first!.Payload.Span).Should().Be("first");
        Encoding.UTF8.GetString(second!.Payload.Span).Should().Be("second");
    }

    [Fact]
    public void TryDecode_WithACorruptLengthPrefix_Throws()
    {
        byte[] garbage = [0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3, 4, 5, 6, 7, 8];

        Action act = () => FrameCodec.TryDecode(garbage, out _, out _);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Encode_EmptyPayload_RoundTrips()
    {
        RawFrame original = Sample([]);
        byte[] buffer = new byte[FrameCodec.MaxEncodedSize(original)];
        int written = FrameCodec.Encode(original, buffer);

        FrameCodec.TryDecode(buffer.AsSpan(0, written), out RawFrame? decoded, out _).Should().BeTrue();
        decoded!.Payload.Length.Should().Be(0);
    }
}
