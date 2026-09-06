using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Ingestion;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Ingestion;

public sealed class SyslogStreamFramerTests
{
    private static List<string> Collect(Action<SyslogStreamFramer, Action<byte[], bool>> feed, int max = 64 * 1024)
    {
        var framer = new SyslogStreamFramer(max);
        var messages = new List<string>();
        void Emit(byte[] payload, bool truncated) => messages.Add(Encoding.UTF8.GetString(payload));
        feed(framer, Emit);
        framer.Flush(Emit);
        return messages;
    }

    [Fact]
    public void NewlineFraming_SplitsOnLineFeed_AndStripsCarriageReturn()
    {
        List<string> msgs = Collect((f, emit) =>
            f.Append(Encoding.UTF8.GetBytes("<13>one\r\n<13>two\n<13>three\n"), emit));

        msgs.Should().Equal("<13>one", "<13>two", "<13>three");
    }

    [Fact]
    public void NewlineFraming_MessageArrivingInPieces_IsAssembled()
    {
        List<string> msgs = Collect((f, emit) =>
        {
            f.Append(Encoding.UTF8.GetBytes("<13>par"), emit);
            f.Append(Encoding.UTF8.GetBytes("tial mess"), emit);
            f.Append(Encoding.UTF8.GetBytes("age\n"), emit);
        });

        msgs.Should().ContainSingle().Which.Should().Be("<13>partial message");
    }

    [Fact]
    public void NewlineFraming_TrailingMessageWithoutNewline_IsFlushedOnClose()
    {
        List<string> msgs = Collect((f, emit) => f.Append(Encoding.UTF8.GetBytes("<13>no newline here"), emit));

        msgs.Should().ContainSingle().Which.Should().Be("<13>no newline here");
    }

    [Fact]
    public void OctetCountedFraming_ReadsExactlyTheDeclaredNumberOfBytes()
    {
        List<string> msgs = Collect((f, emit) =>
            f.Append(Encoding.ASCII.GetBytes("7 <13>abc11 <13>defghij"), emit));

        msgs.Should().Equal("<13>abc", "<13>defghij");
    }

    [Fact]
    public void OctetCountedFraming_LengthAndBodyArrivingSeparately_IsAssembled()
    {
        List<string> msgs = Collect((f, emit) =>
        {
            f.Append(Encoding.ASCII.GetBytes("10 <13>"), emit);
            f.Append(Encoding.ASCII.GetBytes("abcdef"), emit); // only 6 of 10 body bytes -> "13>abcdef" wait
        });

        // "10 " consumed, then "<13>abcdef" = 10 bytes total -> one full message
        msgs.Should().ContainSingle().Which.Should().Be("<13>abcdef");
    }

    [Fact]
    public void OctetCountedFraming_NonNumericLength_ThrowsFramingException()
    {
        var framer = new SyslogStreamFramer(1024);

        Action act = () => framer.Append(Encoding.ASCII.GetBytes("12x <13>hello\n"), (_, _) => { });

        act.Should().Throw<FramingException>();
    }

    [Fact]
    public void Framing_ModeIsChosenFromTheFirstByte_AndFixedForTheConnection()
    {
        var octet = new SyslogStreamFramer(1024);
        octet.Append(Encoding.ASCII.GetBytes("5 hello"), (_, _) => { });
        octet.DetectedFraming.Should().Be("OctetCounted");

        var newline = new SyslogStreamFramer(1024);
        newline.Append(Encoding.ASCII.GetBytes("<13>hello\n"), (_, _) => { });
        newline.DetectedFraming.Should().Be("NewlineDelimited");
    }

    [Fact]
    public void NewlineFraming_LineLongerThanMax_IsTruncatedNotDropped()
    {
        var framer = new SyslogStreamFramer(16);
        var results = new List<(string Text, bool Truncated)>();
        framer.Append(Encoding.ASCII.GetBytes(new string('A', 40) + "\n<13>short\n"),
            (p, t) => results.Add((Encoding.ASCII.GetString(p), t)));

        results[0].Truncated.Should().BeTrue();
        results[0].Text.Should().HaveLength(16);
        results.Should().Contain(r => r.Text == "<13>short" && !r.Truncated);
    }

    [Fact]
    public void OctetCountedFraming_BodyLongerThanMax_IsTruncatedButStreamStaysInSync()
    {
        var framer = new SyslogStreamFramer(8);
        var results = new List<(string Text, bool Truncated)>();
        // 20-byte body then a normal 5-byte one
        framer.Append(Encoding.ASCII.GetBytes("20 " + new string('B', 20) + "5 <13>x"),
            (p, t) => results.Add((Encoding.ASCII.GetString(p), t)));

        results.Should().HaveCount(2);
        results[0].Truncated.Should().BeTrue();
        results[0].Text.Should().HaveLength(8);
        results[1].Text.Should().Be("<13>x");
    }

    [Fact]
    public void NewlineFraming_EmptyKeepAliveLines_AreIgnored()
    {
        List<string> msgs = Collect((f, emit) => f.Append(Encoding.ASCII.GetBytes("\n\n<13>real\n\n"), emit));

        msgs.Should().ContainSingle().Which.Should().Be("<13>real");
    }
}
