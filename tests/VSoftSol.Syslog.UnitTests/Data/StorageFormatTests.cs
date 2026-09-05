using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Repositories;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Data;

public sealed class StorageFormatTests
{
    [Fact]
    public void Timestamp_RoundTrips_PreservingUtcInstant()
    {
        var original = new DateTimeOffset(2026, 3, 14, 15, 9, 26, 535, TimeSpan.FromHours(-5));

        string stored = StorageFormat.Timestamp(original);
        DateTimeOffset parsed = StorageFormat.ParseTimestamp(stored);

        stored.Should().EndWith("Z");
        parsed.Should().Be(original.ToUniversalTime());
        parsed.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(Protocol.Udp, "udp")]
    [InlineData(Protocol.Tcp, "tcp")]
    [InlineData(Protocol.Tls, "tls")]
    [InlineData(Protocol.Snmp, "snmp")]
    [InlineData(Protocol.WinEventLog, "wineventlog")]
    public void Protocol_RoundTripsThroughToken(Protocol protocol, string token)
    {
        StorageFormat.Protocol(protocol).Should().Be(token);
        StorageFormat.ParseProtocol(token).Should().Be(protocol);
    }

    [Theory]
    [InlineData(ParseStatus.Raw, "raw")]
    [InlineData(ParseStatus.Rfc3164, "rfc3164")]
    [InlineData(ParseStatus.Rfc5424, "rfc5424")]
    public void ParseStatus_RoundTripsThroughToken(ParseStatus status, string token)
    {
        StorageFormat.ParseStatus(status).Should().Be(token);
        StorageFormat.ParseParseStatus(token).Should().Be(status);
    }

    [Fact]
    public void RawText_OnInvalidUtf8_ProducesReplacementCharsWithoutThrowing()
    {
        byte[] invalid = [0xFF, 0xFE, 0x41, 0x00, 0xC3, 0x28];

        string text = StorageFormat.RawText(invalid);

        text.Should().Contain("A");
        text.Should().Contain("�");
    }

    [Fact]
    public void RawText_OnValidUtf8_IsExact()
    {
        StorageFormat.RawText(Encoding.UTF8.GetBytes("héllo 世界")).Should().Be("héllo 世界");
    }
}
