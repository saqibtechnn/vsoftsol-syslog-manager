using FluentAssertions;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>PHASE_03 "Year-rollover test for RFC 3164 timestamps on 31 Dec / 1 Jan."</summary>
[Trait("Category", "Parsing")]
public sealed class YearRolloverTests
{
    private readonly Rfc3164Parser _parser = new();

    [Fact]
    public void DecemberMessageReceivedInJanuary_IsDatedToThePreviousYear()
    {
        var received = new DateTimeOffset(2027, 1, 1, 0, 5, 0, TimeSpan.Zero);
        _parser.TryParse("<13>Dec 31 23:59:30 host app: end of year", received, out SyslogParseResult r).Should().BeTrue();

        r.EventUtc!.Value.Year.Should().Be(2026);
        r.EventUtc.Value.Month.Should().Be(12);
        r.EventUtc.Value.Day.Should().Be(31);
    }

    [Fact]
    public void JanuaryMessageReceivedOnNewYearsEve_IsDatedToTheNextYear()
    {
        var received = new DateTimeOffset(2026, 12, 31, 23, 59, 55, TimeSpan.Zero);
        _parser.TryParse("<13>Jan  1 00:00:02 host app: happy new year", received, out SyslogParseResult r).Should().BeTrue();

        r.EventUtc!.Value.Year.Should().Be(2027);
        r.EventUtc.Value.Month.Should().Be(1);
        r.EventUtc.Value.Day.Should().Be(1);
    }

    [Fact]
    public void SameDayMessage_UsesTheReceiptYear()
    {
        var received = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        _parser.TryParse("<13>Jun 15 11:59:59 host app: now", received, out SyslogParseResult r).Should().BeTrue();

        r.EventUtc!.Value.Year.Should().Be(2026);
    }

    [Fact]
    public void Feb29InANonLeapReceiptYear_DoesNotThrow_AndStillParses()
    {
        var received = new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Action act = () => _parser.TryParse("<13>Feb 29 12:00:00 host app: leap", received, out _);
        act.Should().NotThrow();
    }
}
