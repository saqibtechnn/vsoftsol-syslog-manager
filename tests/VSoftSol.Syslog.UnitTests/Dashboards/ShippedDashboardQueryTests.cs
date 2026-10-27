using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Data.Seed;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

/// <summary>
/// Severity compares on the syslog code, where 0 is the MOST severe, so "critical or worse" is
/// <c>severity:&lt;=critical</c>. The shipped "Recent criticals" widget used <c>&gt;=critical</c>, which
/// matched everything from critical down to debug — an "alert" widget full of informational noise.
/// </summary>
public sealed class ShippedDashboardQueryTests
{
    private static SyslogEvent EventWith(Severity severity) => new()
    {
        EventId = 1,
        ReceivedUtc = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
        SourceIp = "10.0.0.9",
        Hostname = "edge-fw-1",
        Facility = Facility.Kernel,
        Severity = severity,
        Protocol = Protocol.Udp,
        Message = "something happened",
        RawMessage = Encoding.UTF8.GetBytes("something happened"),
        ParseStatus = ParseStatus.Rfc3164,
    };

    private static bool Matches(string query, Severity severity)
    {
        SearchParseResult parsed = SearchQueryParser.Parse(query);
        parsed.Success.Should().BeTrue(because: parsed.Error);
        return QueryEvaluator.Matches(parsed.Query!, EventWith(severity), QueryEvaluationContext.Empty);
    }

    [Theory]
    [InlineData(Severity.Emergency, true)]
    [InlineData(Severity.Alert, true)]
    [InlineData(Severity.Critical, true)]
    [InlineData(Severity.Error, false)]
    [InlineData(Severity.Warning, false)]
    [InlineData(Severity.Informational, false)]
    [InlineData(Severity.Debug, false)]
    public void RecentCriticalsWidget_MatchesOnlyCriticalOrWorse(Severity severity, bool expected)
    {
        DashboardDefinition network = DefaultDashboards.All.Single(d => d.SystemKey == "network-overview");
        WidgetDefinition widget = network.Widgets.Single(w => w.Id == "net-criticals");

        Matches(widget.Source.InlineQuery!, severity).Should().Be(expected);
    }
}
