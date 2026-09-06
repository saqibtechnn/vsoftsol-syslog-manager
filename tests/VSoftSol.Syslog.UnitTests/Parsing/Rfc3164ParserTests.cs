using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class Rfc3164ParserTests
{
    private readonly Rfc3164Parser _parser = new();
    private static readonly DateTimeOffset Received = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private SyslogParseResult Parse(string text, DateTimeOffset? received = null)
    {
        _parser.TryParse(text, received ?? Received, out SyslogParseResult r).Should().BeTrue("'{0}' should parse as RFC 3164", text);
        return r;
    }

    [Fact]
    public void TryParse_TheRfc3164Example_ExtractsPriorityTimestampHostTagMessage()
    {
        SyslogParseResult r = Parse("<34>Oct 11 22:14:15 mymachine su: 'su root' failed for lonvick on /dev/pts/8");

        r.Status.Should().Be(ParseStatus.Rfc3164);
        r.Facility.Should().Be(Facility.Security);
        r.Severity.Should().Be(Severity.Critical);
        r.EventUtc!.Value.Month.Should().Be(10);
        r.EventUtc.Value.Day.Should().Be(11);
        r.EventUtc.Value.Hour.Should().Be(22);
        r.TimestampAmbiguous.Should().BeTrue("BSD timestamps carry no timezone");
        r.Hostname.Should().Be("mymachine");
        r.AppName.Should().Be("su");
        r.Message.Should().Be("'su root' failed for lonvick on /dev/pts/8");
    }

    [Fact]
    public void TryParse_TagWithPid_SeparatesAppNameAndProcId()
    {
        SyslogParseResult r = Parse("<38>Oct 12 09:15:00 web01 sshd[1234]: Accepted publickey for admin from 10.0.0.5");

        r.AppName.Should().Be("sshd");
        r.ProcId.Should().Be("1234");
        r.Hostname.Should().Be("web01");
        r.Message.Should().Be("Accepted publickey for admin from 10.0.0.5");
    }

    [Fact]
    public void TryParse_SpacePaddedSingleDigitDay_IsAccepted()
    {
        SyslogParseResult r = Parse("<13>Mar  1 08:00:00 host app: hello");

        r.EventUtc!.Value.Day.Should().Be(1);
        r.EventUtc.Value.Month.Should().Be(3);
    }

    [Fact]
    public void TryParse_CiscoIosSequenceNumberAndSubsecondTimestamp_IsHandled()
    {
        SyslogParseResult r = Parse("<189>44444: Mar  1 22:14:15.003: %SYS-5-CONFIG_I: Configured from console by admin on vty0");

        r.EventUtc!.Value.Millisecond.Should().Be(3);
        r.Message.Should().Be("%SYS-5-CONFIG_I: Configured from console by admin on vty0");
    }

    [Fact]
    public void TryParse_NoTimestampButHasPriority_StillParsesAsRfc3164()
    {
        SyslogParseResult r = Parse("<34>router1 %LINK-3-UPDOWN: Interface down");

        r.Status.Should().Be(ParseStatus.Rfc3164);
        r.EventUtc.Should().BeNull();
        r.TimestampAmbiguous.Should().BeFalse();
    }

    [Theory]
    [InlineData("plain text with no priority and no timestamp")]
    [InlineData("")]
    public void TryParse_NoPriorityNoTimestamp_ReturnsFalse(string text)
    {
        _parser.TryParse(text, Received, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_EmbeddedNewlineInMessage_FlagsFramingAnomaly()
    {
        SyslogParseResult r = Parse("<13>Oct 12 09:00:00 host app: real\ninjected");

        r.FramingAnomaly.Should().BeTrue();
    }

    [Fact]
    public void TryParse_NeverThrows_ForAnyPrefixOfAValidMessage()
    {
        const string full = "<38>Oct 12 09:15:00 web01 sshd[1234]: Accepted publickey for admin from 10.0.0.5";
        for (int i = 0; i <= full.Length; i++)
        {
            Action act = () => _parser.TryParse(full[..i], Received, out _);
            act.Should().NotThrow("prefix length {0}", i);
        }
    }
}
