using FluentAssertions;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Data.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

/// <summary>
/// The Phase 9 aggregation compiler resolves group-by columns through its own
/// <c>EventColumns</c> allow-list; the Phase 8 alert window reader has its own. They must
/// agree on which columns are groupable, or an alert and a dashboard that "group by the
/// same field" would silently disagree.
/// </summary>
public sealed class EventColumnsParityTests
{
    [Theory]
    [InlineData("hostname")]
    [InlineData("source_ip")]
    [InlineData("app")]
    [InlineData("proc_id")]
    [InlineData("msg_id")]
    [InlineData("severity")]
    [InlineData("facility")]
    [InlineData("vendor")]
    [InlineData("protocol")]
    [InlineData("parse_status")]
    public void EveryDashboardGroupableColumn_IsAlsoAlertGroupable(string field)
    {
        EventColumns.Groupable.Should().ContainKey(field);
        SqliteAlertWindowReader.IsSqlGroupable(field).Should().BeTrue(field);
    }

    [Fact]
    public void NeitherSideGroupsByMessage()
    {
        EventColumns.Groupable.Should().NotContainKey("message");
        SqliteAlertWindowReader.IsSqlGroupable("message").Should().BeFalse();
    }

    [Fact]
    public void TheTwoGroupableSetsAreTheSameSize()
    {
        // If Phase 8 adds a groupable column, this fails until Phase 9's map is updated too.
        string[] candidates =
        [
            "hostname", "source_ip", "app", "proc_id", "msg_id", "severity",
            "facility", "vendor", "protocol", "parse_status", "message", "event_id", "device_id",
        ];

        int dashboardCount = candidates.Count(EventColumns.Groupable.ContainsKey);
        int alertCount = candidates.Count(SqliteAlertWindowReader.IsSqlGroupable);

        dashboardCount.Should().Be(alertCount);
        dashboardCount.Should().Be(EventColumns.Groupable.Count);
    }
}
