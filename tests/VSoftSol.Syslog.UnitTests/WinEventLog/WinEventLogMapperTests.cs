using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.WinEventLog;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.WinEventLog;

[Trait("Category", "WinEventLog")]
public sealed class WinEventLogMapperTests
{
    [Theory]
    [InlineData(1, Severity.Critical)]
    [InlineData(2, Severity.Error)]
    [InlineData(3, Severity.Warning)]
    [InlineData(4, Severity.Informational)]
    [InlineData(5, Severity.Debug)]
    [InlineData(0, Severity.Informational)]
    [InlineData(99, Severity.Informational)]
    public void SeverityFor_MapsEveryWindowsLevel(int level, Severity expected) =>
        WinEventLogMapper.SeverityFor(level).Should().Be(expected);

    [Theory]
    [InlineData("Security", Facility.SecurityAuth)]
    [InlineData("System", Facility.Syslogd)]
    [InlineData("Application", Facility.User)]
    [InlineData("Microsoft-Windows-TaskScheduler/Operational", Facility.Local7)]
    public void FacilityFor_MapsKnownChannels_AndFallsBackForCustomOnes(string channel, Facility expected) =>
        WinEventLogMapper.FacilityFor(channel).Should().Be(expected);
}
