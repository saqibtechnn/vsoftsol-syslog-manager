using FluentAssertions;
using VSoftSol.Syslog.Web.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hosting;

/// <summary>
/// PHASE_12 build item 1 — per ADR 0005 ("One Windows Service hosting collector and UI, not
/// split processes"), the packaged product installs exactly one Windows Service, and
/// <c>VSoftSol.Syslog.Web.exe</c> is that single production entry point (it already hosts
/// Kestrel/Blazor; conditionally hosting the collector too — see
/// <c>CollectorOptions.HostCollectorRuntime</c> — avoids the circular project reference a
/// Service-hosts-Web merge would require, since Web already references Service for the
/// composition root). This constant is the SCM name Program.cs registers via
/// <c>UseWindowsService</c>; it must exactly match the <c>ServiceInstall/@Name</c> the WiX
/// installer creates in <c>installer/Product.wxs</c>, or the service fails to start
/// (mismatched internal SCM name vs. what <see cref="System.ServiceProcess.ServiceBase"/>
/// registers with <c>StartServiceCtrlDispatcher</c>).
/// </summary>
[Trait("Category", "Hosting")]
public sealed class WebServiceIdentityTests
{
    [Fact]
    public void ServiceName_Always_MatchesTheNameTheInstallerRegisters()
    {
        WebServiceIdentity.ServiceName.Should().Be("VSoftSol Syslog Manager");
    }
}
