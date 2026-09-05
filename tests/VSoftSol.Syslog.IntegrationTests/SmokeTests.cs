using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests;

/// <summary>Proves the integration-test harness runs.</summary>
public sealed class SmokeTests
{
    [Fact]
    public void Harness_Runs_True()
    {
        DateTimeOffset.UtcNow.Should().BeAfter(DateTimeOffset.UnixEpoch);
    }
}
