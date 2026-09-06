using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class Rfc5424ParserTests
{
    private readonly Rfc5424Parser _parser = new();
    private static readonly DateTimeOffset Received = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private SyslogParseResult Parse(string text)
    {
        _parser.TryParse(text, Received, out SyslogParseResult r).Should().BeTrue("'{0}' should parse as RFC 5424", text);
        return r;
    }

    [Fact]
    public void TryParse_TheRfc5424Example1_ExtractsEveryHeaderField()
    {
        SyslogParseResult r = Parse("<34>1 2003-10-11T22:14:15.003Z mymachine.example.com su - ID47 - 'su root' failed for lonvick on /dev/pts/8");

        r.Status.Should().Be(ParseStatus.Rfc5424);
        r.Facility.Should().Be(Facility.Security); // PRI 34 => facility 4
        r.Severity.Should().Be(Severity.Critical);
        r.EventUtc.Should().Be(new DateTimeOffset(2003, 10, 11, 22, 14, 15, 3, TimeSpan.Zero));
        r.Hostname.Should().Be("mymachine.example.com");
        r.AppName.Should().Be("su");
        r.ProcId.Should().BeNull();
        r.MsgId.Should().Be("ID47");
        r.StructuredDataJson.Should().BeNull();
        r.Message.Should().Be("'su root' failed for lonvick on /dev/pts/8");
    }

    [Fact]
    public void TryParse_WithStructuredData_SerialisesElementsToJson()
    {
        SyslogParseResult r = Parse(
            "<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 " +
            "[exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"][examplePriority@32473 class=\"high\"] BOMAn application event log entry...");

        r.StructuredDataJson.Should().Be(
            "{\"exampleSDID@32473\":{\"iut\":\"3\",\"eventSource\":\"Application\",\"eventID\":\"1011\"},\"examplePriority@32473\":{\"class\":\"high\"}}");
        r.Message.Should().Be("BOMAn application event log entry...");
    }

    [Fact]
    public void TryParse_StructuredDataWithEscapedQuoteAndBracket_Unescapes()
    {
        SyslogParseResult r = Parse(
            "<165>1 2003-10-11T22:14:15.003Z host app - - [ex@1 k=\"a \\\"quoted\\\" ] value\"] msg");

        // The SD value contained an escaped quote and an escaped ']'; both survive into JSON.
        r.StructuredDataJson.Should().Contain("quoted").And.Contain("] value");
        r.Message.Should().Be("msg");
    }

    [Fact]
    public void TryParse_NilTimestamp_LeavesEventUtcNull()
    {
        SyslogParseResult r = Parse("<34>1 - host app 123 msgid - the message");

        r.EventUtc.Should().BeNull();
        r.ProcId.Should().Be("123");
        r.Message.Should().Be("the message");
    }

    [Fact]
    public void TryParse_TimestampWithOffset_NormalisesToUtc()
    {
        SyslogParseResult r = Parse("<34>1 2026-03-01T10:00:00+02:00 host app - - - x");

        r.EventUtc.Should().Be(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("not syslog at all")]
    [InlineData("<34> 1 2003-10-11T22:14:15Z h a p m -")]        // space after PRI
    [InlineData("<34>2 2003-10-11T22:14:15Z h a p m - msg")]     // version 2
    [InlineData("<999>1 - h a p m - msg")]                        // PRI out of range
    [InlineData("<34>1 2003-13-99T99:99:99Z h a p m - msg")]      // present but invalid timestamp
    [InlineData("<34>1 - host")]                                  // header truncated
    public void TryParse_NonRfc5424Input_ReturnsFalse(string text)
    {
        _parser.TryParse(text, Received, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_MessageWithEmbeddedNewline_FlagsFramingAnomaly_ButStillOneResult()
    {
        SyslogParseResult r = Parse("<34>1 2026-03-01T00:00:00Z h a - - - line one\nfake line two");

        r.FramingAnomaly.Should().BeTrue();
        r.Message.Should().Be("line one\nfake line two");
    }

    [Fact]
    public void TryParse_NeverThrows_ForAnyPrefixOfAValidMessage()
    {
        const string full = "<165>1 2003-10-11T22:14:15.003Z host app 1 id [a@1 k=\"v\"] the body";
        for (int i = 0; i <= full.Length; i++)
        {
            Action act = () => _parser.TryParse(full[..i], Received, out _);
            act.Should().NotThrow("prefix length {0}", i);
        }
    }
}
