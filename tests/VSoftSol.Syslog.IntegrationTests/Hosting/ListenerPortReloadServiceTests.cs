using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hosting;

/// <summary>
/// v1.1 — live listener port changes (closes the RELEASE_NOTES.md v1.0.0 known limitation
/// "listener port changes need a manual service restart", UDP/TCP scope). Orchestrates
/// <see cref="UdpSyslogListener.RebindAsync"/>/<see cref="TcpSyslogListener.RebindAsync"/>,
/// persisting the new port via <see cref="BootstrapConfigOverrides.UpdateIngestionPortsAsync"/>
/// only after a successful rebind, and keeping the rule engine's own-listener loop guard
/// (<see cref="ActionExecutorOptions.LocalSyslogEndpoints"/>) in step.
/// </summary>
[Trait("Category", "Hosting")]
public sealed class ListenerPortReloadServiceTests
{
    private static string NewTempDataDir() => Path.Combine(Path.GetTempPath(), "vsoftsol-portreload-" + Guid.NewGuid().ToString("N"));

    private static async Task<(IngestionHarness Harness, UdpSyslogListener Udp, TcpSyslogListener Tcp)> StartedListenersAsync(int udpPort, int tcpPort)
    {
        IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = udpPort;
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = tcpPort;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        TcpSyslogListener tcp = h.AddTcpListener();
        await udp.StartAsync(default);
        await tcp.StartAsync(default);
        h.StartPipeline();
        return (h, udp, tcp);
    }

    private static ListenerPortReloadService BuildService(
        IngestionHarness h, IReadOnlyList<ISyslogListener> listeners, ActionExecutorOptions actionOptions, string dataDirectory) =>
        new(
            listeners,
            Options.Create(h.Options),
            Options.Create(actionOptions),
            Options.Create(new CollectorOptions { DataDirectory = dataDirectory }),
            NullLogger<ListenerPortReloadService>.Instance);

    [Fact]
    public void CanApplyLive_NoUdpOrTcpListenersRegistered_IsFalse()
    {
        var service = new ListenerPortReloadService(
            [],
            Options.Create(new IngestionOptions()),
            Options.Create(new ActionExecutorOptions()),
            Options.Create(new CollectorOptions { DataDirectory = NewTempDataDir() }),
            NullLogger<ListenerPortReloadService>.Instance);

        service.CanApplyLive.Should().BeFalse("a Web-standalone process (no collector runtime) hosts no listener to rebind");
    }

    [Fact]
    public async Task ApplyAsync_NewUdpPort_RebindsLive_PersistsToOverrideFile_AndUpdatesLoopGuard()
    {
        int udpPort = LoopbackSyslog.FreeUdpPort();
        int tcpPort = LoopbackSyslog.FreeTcpPort();
        int newUdpPort = LoopbackSyslog.FreeUdpPort();
        string dataDir = NewTempDataDir();

        (IngestionHarness h, UdpSyslogListener udp, TcpSyslogListener tcp) = await StartedListenersAsync(udpPort, tcpPort);
        await using (h)
        {
            var actionOptions = new ActionExecutorOptions
            {
                LocalSyslogEndpoints = [$"127.0.0.1:{udpPort}", $"127.0.0.1:{tcpPort}"],
            };
            ListenerPortReloadService service = BuildService(h, [udp, tcp], actionOptions, dataDir);

            try
            {
                PortReloadResult result = await service.ApplyAsync(newUdpPort, null, CancellationToken.None);

                result.Success.Should().BeTrue();
                udp.BoundPort.Should().Be(newUdpPort);
                tcp.BoundPort.Should().Be(tcpPort, "only the UDP port was requested to change");

                actionOptions.LocalSyslogEndpoints.Should().Contain($"127.0.0.1:{newUdpPort}")
                    .And.NotContain($"127.0.0.1:{udpPort}", "the rule engine's own-listener loop guard must track the live port, not the stale one");

                h.Options.UdpPort.Should().Be(newUdpPort,
                    "anything else reading the shared IngestionOptions (e.g. the Settings page) must see the live port too");

                Microsoft.Extensions.Configuration.ConfigurationBuilder builder = new();
                BootstrapConfigOverrides.Apply(builder, dataDir);
                Microsoft.Extensions.Configuration.IConfigurationRoot configuration = builder.Build();
                configuration["Ingestion:UdpPort"].Should().Be(newUdpPort.ToString());
            }
            finally
            {
                await udp.StopAsync(default);
                await tcp.StopAsync(default);
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task ApplyAsync_PortAlreadyInUse_ReturnsFailure_LeavesOriginalPortAndFileUntouched()
    {
        int udpPort = LoopbackSyslog.FreeUdpPort();
        int tcpPort = LoopbackSyslog.FreeTcpPort();
        int busyPort = LoopbackSyslog.FreeUdpPort();
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, busyPort));
        string dataDir = NewTempDataDir();

        (IngestionHarness h, UdpSyslogListener udp, TcpSyslogListener tcp) = await StartedListenersAsync(udpPort, tcpPort);
        await using (h)
        {
            var actionOptions = new ActionExecutorOptions { LocalSyslogEndpoints = [$"127.0.0.1:{udpPort}"] };
            ListenerPortReloadService service = BuildService(h, [udp, tcp], actionOptions, dataDir);

            try
            {
                PortReloadResult result = await service.ApplyAsync(busyPort, null, CancellationToken.None);

                result.Success.Should().BeFalse();
                result.Errors.Should().ContainSingle();
                udp.BoundPort.Should().Be(udpPort, "a failed rebind must leave the working listener exactly as it was");
                actionOptions.LocalSyslogEndpoints.Should().Contain($"127.0.0.1:{udpPort}");

                Directory.Exists(dataDir).Should().BeFalse("nothing succeeded, so nothing should have been persisted");
            }
            finally
            {
                await udp.StopAsync(default);
                await tcp.StopAsync(default);
            }
        }
    }

    [Fact]
    public async Task ApplyAsync_NotApplicable_WhenNoListenersHostedInThisProcess()
    {
        string dataDir = NewTempDataDir();
        var service = new ListenerPortReloadService(
            [],
            Options.Create(new IngestionOptions()),
            Options.Create(new ActionExecutorOptions()),
            Options.Create(new CollectorOptions { DataDirectory = dataDir }),
            NullLogger<ListenerPortReloadService>.Instance);

        PortReloadResult result = await service.ApplyAsync(5514, 5515, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.NotApplicable.Should().BeTrue();
        Directory.Exists(dataDir).Should().BeFalse();
    }
}
