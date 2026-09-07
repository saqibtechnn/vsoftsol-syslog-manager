using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Search;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Search;

/// <summary>
/// The golden-oracle matcher. These lock its semantics; the 500-query differential test
/// (integration) then proves the SQL compiler agrees with it.
/// </summary>
public sealed class QueryEvaluatorTests
{
    private static SyslogEvent Event(
        string message = "Failed password for root from 10.0.0.9 port 22",
        string sourceIp = "10.0.0.9",
        string? hostname = "edge-fw-1",
        string? app = "sshd",
        Severity severity = Severity.Warning,
        Facility facility = Facility.SecurityAuth,
        string? vendor = "linux",
        Protocol protocol = Protocol.Udp,
        ParseStatus parseStatus = ParseStatus.Rfc3164,
        long? deviceId = 4,
        params EventField[] fields) => new()
        {
            EventId = 42,
            ReceivedUtc = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            EventUtc = new DateTimeOffset(2026, 9, 1, 9, 59, 0, TimeSpan.Zero),
            SourceIp = sourceIp,
            Hostname = hostname,
            AppName = app,
            Facility = facility,
            Severity = severity,
            Protocol = protocol,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = parseStatus,
            DeviceId = deviceId,
            Vendor = vendor,
            Fields = fields,
        };

    private static bool Match(string query, SyslogEvent evt, QueryEvaluationContext? ctx = null)
    {
        SearchParseResult parsed = SearchQueryParser.Parse(query);
        parsed.Success.Should().BeTrue(because: parsed.Error);
        return QueryEvaluator.Matches(parsed.Query!, evt, ctx ?? QueryEvaluationContext.Empty);
    }

    [Fact]
    public void MatchAll_MatchesEverything() =>
        Match("", Event()).Should().BeTrue();

    [Theory]
    [InlineData("failed", true)]
    [InlineData("FAILED", true)]
    [InlineData("\"failed password\"", true)]
    [InlineData("\"password failed\"", false)] // phrase order matters
    [InlineData("fail*", true)]
    [InlineData("xyzzy", false)]
    public void FreeText_MatchesTokensAndPhrases(string query, bool expected) =>
        Match(query, Event()).Should().Be(expected);

    [Theory]
    [InlineData("host:edge-fw-1", true)]
    [InlineData("host:EDGE-FW-1", true)]
    [InlineData("host:edge*", true)]
    [InlineData("host:core*", false)]
    [InlineData("host:!=edge-fw-1", false)]
    [InlineData("source_ip:10.0.0.9", true)]
    [InlineData("ip:10.0.0.*", true)]
    [InlineData("app:sshd", true)]
    [InlineData("vendor:linux", true)]
    [InlineData("protocol:udp", true)]
    [InlineData("protocol:tcp", false)]
    [InlineData("parse_status:rfc3164", true)]
    public void FieldTerm_StringFields(string query, bool expected) =>
        Match(query, Event()).Should().Be(expected);

    [Fact]
    public void FieldTerm_NotEquals_DoesNotMatchNullColumn() =>
        Match("app:!=telnetd", Event(app: null)).Should().BeFalse();

    [Theory]
    [InlineData("severity:warning", true)]
    [InlineData("severity:4", true)]
    [InlineData("severity:error", false)]
    [InlineData("severity:>=warning", true)]
    [InlineData("severity:<warning", false)]
    [InlineData("severity:>notice", false)] // notice=5 > warning=4
    [InlineData("facility:authpriv", true)] // SecurityAuth = 10
    [InlineData("facility:10", true)]
    [InlineData("facility:<10", false)]
    [InlineData("event_id:42", true)]
    [InlineData("event_id:>40", true)]
    [InlineData("device_id:4", true)]
    [InlineData("device_id:>=5", false)]
    public void FieldTerm_NumericFields(string query, bool expected) =>
        Match(query, Event()).Should().Be(expected);

    [Theory]
    [InlineData("received:>=2026-09-01T00:00:00Z", true)]
    [InlineData("received:<2026-09-01T00:00:00Z", false)]
    [InlineData("event_time:<2026-09-01T09:59:30Z", true)]
    public void FieldTerm_TimestampFields(string query, bool expected) =>
        Match(query, Event()).Should().Be(expected);

    [Theory]
    [InlineData("field.port:22", true)]
    [InlineData("field.port:23", false)]
    [InlineData("field.port:>20", true)]
    [InlineData("field.user:root", true)]
    [InlineData("field.user:r*", true)]
    [InlineData("field.missing:x", false)]
    [InlineData("field.missing:!=x", true)] // NOT EXISTS semantics
    public void FieldTerm_CustomFields(string query, bool expected) =>
        Match(query, Event(fields: [new EventField("port", "22"), new EventField("user", "root")]))
            .Should().Be(expected);

    [Theory]
    [InlineData("device:edge-fw-1", true)]
    [InlineData("device:other", false)]
    [InlineData("stream:Firewall", true)]
    [InlineData("stream:Windows", false)]
    [InlineData("stream:!=Windows", true)]
    public void FieldTerm_ReferenceFields_UseContext(string query, bool expected)
    {
        var ctx = new QueryEvaluationContext
        {
            DeviceName = "edge-fw-1",
            StreamNames = ["Firewall", "All Events"],
        };

        Match(query, Event(), ctx).Should().Be(expected);
    }

    [Theory]
    [InlineData("failed AND root", true)]
    [InlineData("failed AND success", false)]
    [InlineData("failed OR success", true)]
    [InlineData("NOT failed", false)]
    [InlineData("NOT success", true)]
    [InlineData("failed -success", true)]
    [InlineData("(failed OR success) AND root", true)]
    [InlineData("severity:error OR host:edge-fw-1", true)]
    public void BooleanComposition(string query, bool expected) =>
        Match(query, Event()).Should().Be(expected);
}
