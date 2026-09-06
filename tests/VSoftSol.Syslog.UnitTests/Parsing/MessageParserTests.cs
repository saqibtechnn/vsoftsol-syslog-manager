using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class MessageParserTests
{
    private static MessageParser Build(ParsingOptions? o = null) => ParsingComposition.Build(o).Parser;

    private static RawFrame Frame(string wire, string sourceIp = "198.51.100.7", bool truncated = false) => new(
        new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), sourceIp, "udp:test", Protocol.Udp,
        Encoding.UTF8.GetBytes(wire), truncated);

    private static RawFrame FrameBytes(byte[] wire, string sourceIp = "198.51.100.7") => new(
        new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), sourceIp, "udp:test", Protocol.Udp, wire, false);

    [Fact]
    public void Parse_Rfc5424Message_ProducesRfc5424Event()
    {
        SyslogEvent e = Build().Parse(Frame("<34>1 2026-03-01T10:00:00Z host app 1 ID - hello world"));

        e.ParseStatus.Should().Be(ParseStatus.Rfc5424);
        e.Hostname.Should().Be("host");
        e.Message.Should().Be("hello world");
        e.SourceIp.Should().Be("198.51.100.7");
    }

    [Fact]
    public void Parse_Rfc3164Message_ProducesRfc3164Event()
    {
        SyslogEvent e = Build().Parse(Frame("<38>Oct 12 09:15:00 web01 sshd[1234]: Accepted publickey"));

        e.ParseStatus.Should().Be(ParseStatus.Rfc3164);
        e.AppName.Should().Be("sshd");
        e.ProcId.Should().Be("1234");
    }

    [Fact]
    public void Parse_Unparseable_ProducesRawEvent_ThatStillKeepsSourceIpAndRawBytes()
    {
        var frame = Frame("this is just free text from some appliance");
        SyslogEvent e = Build().Parse(frame);

        e.ParseStatus.Should().Be(ParseStatus.Raw);
        e.Message.Should().BeEmpty("raw events carry no parsed message body");
        e.SourceIp.Should().Be("198.51.100.7");
        e.RawMessage.ToArray().Should().Equal(frame.Payload.ToArray());
    }

    [Fact]
    public void Parse_HostnameClaimedInMessage_DoesNotOverrideTheWireObservedSourceIp()
    {
        SyslogEvent e = Build().Parse(Frame("<34>1 2026-03-01T10:00:00Z totally-fake-host app - - - x", sourceIp: "10.0.0.9"));

        e.SourceIp.Should().Be("10.0.0.9", "the wire-observed source IP always wins");
        e.Hostname.Should().Be("totally-fake-host");
    }

    [Theory]
    [InlineData("<9")]                               // truncated priority
    [InlineData("<34>1 ")]                            // 5424 header only
    [InlineData("")]                                  // empty
    [InlineData("<34>1 - - - - - - ")]                // all-nil 5424
    public void Parse_MalformedInput_NeverThrows_AndAlwaysReturnsAnEvent(string wire)
    {
        Action act = () => Build().Parse(Frame(wire));
        act.Should().NotThrow();
        Build().Parse(Frame(wire)).RawMessage.Length.Should().Be(Encoding.UTF8.GetByteCount(wire));
    }

    [Fact]
    public void Parse_InvalidUtf8Payload_StoresRawBytesVerbatim_AndFlagsCharset()
    {
        byte[] wire = [(byte)'<', (byte)'1', (byte)'3', (byte)'>', (byte)' ', 0xE9, 0xE9];
        SyslogEvent e = Build().Parse(FrameBytes(wire));

        e.RawMessage.ToArray().Should().Equal(wire);
        e.Fields.Should().Contain(f => f.Name == "charset" && f.Value == "latin-1");
    }

    [Fact]
    public void Parse_OversizedFrame_CarriesTheTruncatedField()
    {
        SyslogEvent e = Build().Parse(Frame("<13>Oct 12 09:00:00 h a: msg", truncated: true));

        e.Fields.Should().Contain(f => f.Name == "truncated" && f.Value == "true");
    }

    [Fact]
    public void Parse_EmbeddedNul_KeepsRawBytes_DoesNotSplitIntoMultipleEvents()
    {
        byte[] wire = [.. "<13>Oct 12 09:00:00 h a: before"u8, 0x00, .. "after"u8];
        SyslogEvent e = Build().Parse(FrameBytes(wire));

        e.RawMessage.ToArray().Should().Equal(wire);
    }
}
