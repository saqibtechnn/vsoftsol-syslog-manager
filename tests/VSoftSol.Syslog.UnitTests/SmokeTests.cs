using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.UnitTests;

/// <summary>Proves the unit-test harness runs. Deliberately trivial.</summary>
public sealed class SmokeTests
{
    [Fact]
    public void Harness_Runs_True()
    {
        true.Should().BeTrue();
    }
}
