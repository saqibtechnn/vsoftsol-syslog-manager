using FluentAssertions;
using VSoftSol.Syslog.Web.Setup;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Setup;

/// <summary>PHASE_12 build item 2a — the IP shown in the "Waiting for messages" page's
/// device commands.</summary>
public sealed class ServerAddressResolverTests
{
    [Fact]
    public void Resolve_ARealLanHostname_ReturnsItUnchanged()
    {
        ServerAddressResolver.Resolve("syslog01.corp.example").Should().Be("syslog01.corp.example");
    }

    [Fact]
    public void Resolve_ARealLanIpAddress_ReturnsItUnchanged()
    {
        ServerAddressResolver.Resolve("10.20.30.40").Should().Be("10.20.30.40");
    }

    [Fact]
    public void Resolve_Localhost_DoesNotReturnLocalhostVerbatim()
    {
        ServerAddressResolver.Resolve("localhost").Should().NotBe("localhost");
    }

    [Fact]
    public void Resolve_LoopbackIp_DoesNotReturnALoopbackAddress()
    {
        string resolved = ServerAddressResolver.Resolve("127.0.0.1");

        resolved.Should().NotBe("127.0.0.1");
    }
}
