using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// RFC 3164 messages often carry no hostname. The dashboards then showed "(none)" as the busiest
/// "device" and a dash in the Host column, which tells an admin nothing; the source address is the
/// next-best identity and is always known.
/// </summary>
public sealed class HostFallbackTests : IAsyncLifetime
{
    private DashboardTestHarness _h = null!;

    public async Task InitializeAsync()
    {
        _h = await DashboardTestHarness.CreateAsync(seed: false);
        var events = new List<SyslogEvent>();
        events.AddRange(Enumerable.Range(0, 4).Select(i => Event(i, "fw-1", "10.0.0.1")));
        events.AddRange(Enumerable.Range(4, 3).Select(i => Event(i, null, "10.9.9.9")));
        events.AddRange(Enumerable.Range(7, 2).Select(i => Event(i, "", "10.9.9.8")));
        await _h.SeedEventsAsync(events);
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static SyslogEvent Event(int i, string? host, string sourceIp)
    {
        DateTimeOffset at = AggregationFixture.WindowStart.AddMinutes(i);
        string message = $"event {i}";
        return new SyslogEvent
        {
            ReceivedUtc = at,
            EventUtc = at,
            SourceIp = sourceIp,
            Hostname = host,
            Facility = Facility.Kernel,
            Severity = Severity.Informational,
            Protocol = Protocol.Udp,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = ParseStatus.Rfc3164,
        };
    }

    [Fact]
    public async Task GroupByHostname_UsesTheSourceAddress_WhenTheMessageCarriedNoHostname()
    {
        var spec = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 50 };

        var outcome = await _h.Aggregation.AggregateAsync(
            UserScope.Unrestricted, string.Empty, spec,
            AggregationFixture.WindowStart, AggregationFixture.WindowEnd, BucketInterval.None, CancellationToken.None);

        Dictionary<string, double> actual = outcome.Result.Points
            .ToDictionary(p => p.GroupKey ?? "(null)", p => p.Value);
        actual.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["fw-1"] = 4,
            ["10.9.9.9"] = 3,
            ["10.9.9.8"] = 2,
        }, "a missing or empty hostname falls back to the source address, never '(none)'");
    }

    [Theory]
    [InlineData("core-sw-1", "10.0.0.1", "core-sw-1")]
    [InlineData(null, "10.9.9.9", "10.9.9.9")]
    [InlineData("", "10.9.9.9", "10.9.9.9")]
    [InlineData("   ", "10.9.9.9", "10.9.9.9")]
    public void RecentEventRow_HostLabel_FallsBackToTheSourceAddress(string? hostname, string sourceIp, string expected)
    {
        RecentEventRow.HostLabel(hostname, sourceIp).Should().Be(expected);
    }
}
