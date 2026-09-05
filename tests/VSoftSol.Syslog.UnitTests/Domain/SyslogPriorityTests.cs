using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Domain;

public sealed class SyslogPriorityTests
{
    [Theory]
    [InlineData(Facility.Kernel, Severity.Emergency, 0)]
    [InlineData(Facility.User, Severity.Notice, 13)]
    [InlineData(Facility.Local7, Severity.Debug, 191)]
    [InlineData(Facility.Security, Severity.Critical, 34)]
    public void Value_ForKnownPairs_MatchesRfc5424(Facility facility, Severity severity, int expected)
    {
        new SyslogPriority(facility, severity).Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, Facility.Kernel, Severity.Emergency)]
    [InlineData(191, Facility.Local7, Severity.Debug)]
    [InlineData(34, Facility.Security, Severity.Critical)]
    public void FromValue_DecodesToOriginalPair(int priority, Facility facility, Severity severity)
    {
        SyslogPriority result = SyslogPriority.FromValue(priority);

        result.Facility.Should().Be(facility);
        result.Severity.Should().Be(severity);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(192)]
    [InlineData(int.MaxValue)]
    public void FromValue_OutOfRange_Throws(int priority)
    {
        Action act = () => SyslogPriority.FromValue(priority);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
