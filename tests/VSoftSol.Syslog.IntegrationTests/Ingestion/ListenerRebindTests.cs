using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>
/// v1.1 — live listener port changes. <see cref="UdpSyslogListener.RebindAsync"/> and
/// <see cref="TcpSyslogListener.RebindAsync"/> move a running listener to a new port without
/// a process restart: the new port is bound and accepting before the old one is torn down
/// (so a bind failure never leaves the collector with zero listeners on that protocol —
/// CLAUDE.md Constraint 3, never lose a message).
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class ListenerRebindTests
{
    private static async Task WaitForReceivedAsync(IngestionHarness h, long target, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (h.Stats.Snapshot().Total.Received >= target)
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Udp_RebindAsync_MovesToNewPort_OldPortNoLongerAccepts()
    {
        int oldPort = LoopbackSyslog.FreeUdpPort();
        int newPort = LoopbackSyslog.FreeUdpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = oldPort;
        });

        UdpSyslogListener udp = h.AddUdpListener();
        await udp.StartAsync(default);
        h.StartPipeline();

        await LoopbackSyslog.SendUdpAsync(oldPort, 5, prefix: "before");
        await WaitForReceivedAsync(h, 5, TimeSpan.FromSeconds(10));

        await udp.RebindAsync(newPort, default);

        udp.BoundPort.Should().Be(newPort);

        await LoopbackSyslog.SendUdpAsync(newPort, 5, prefix: "after");
        await WaitForReceivedAsync(h, 10, TimeSpan.FromSeconds(10));

        await udp.StopAsync(default);
        await h.DrainAsync();

        h.Stats.Snapshot().Total.Received.Should().Be(10, "5 before the rebind and 5 after, none lost across the swap");

        // The old port must be free again — nothing is still listening on it.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Action bind = () => probe.Bind(new IPEndPoint(IPAddress.Loopback, oldPort));
        bind.Should().NotThrow("the old socket was closed by the rebind, freeing the port");
    }

    [Fact]
    public async Task Udp_RebindAsync_PortAlreadyInUse_ThrowsAndLeavesOriginalListenerRunning()
    {
        int oldPort = LoopbackSyslog.FreeUdpPort();
        int busyPort = LoopbackSyslog.FreeUdpPort();
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, busyPort));

        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = oldPort;
        });

        UdpSyslogListener udp = h.AddUdpListener();
        await udp.StartAsync(default);
        h.StartPipeline();

        Func<Task> rebind = () => udp.RebindAsync(busyPort, default);
        await rebind.Should().ThrowAsync<SocketException>();

        udp.BoundPort.Should().Be(oldPort, "a failed rebind must not disturb the listener that was already working");

        await LoopbackSyslog.SendUdpAsync(oldPort, 3, prefix: "still-alive");
        await WaitForReceivedAsync(h, 3, TimeSpan.FromSeconds(10));
        h.Stats.Snapshot().Total.Received.Should().Be(3, "the original listener kept accepting after the failed rebind");

        await udp.StopAsync(default);
        await h.DrainAsync();
    }

    [Fact]
    public async Task Tcp_RebindAsync_MovesToNewPort_OldConnectionsClosed_NewPortAccepts()
    {
        int oldPort = LoopbackSyslog.FreeTcpPort();
        int newPort = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = oldPort;
        });

        TcpSyslogListener tcp = h.AddTcpListener();
        await tcp.StartAsync(default);
        h.StartPipeline();

        await LoopbackSyslog.SendTcpNewlineAsync(oldPort, 4, prefix: "before");
        await WaitForReceivedAsync(h, 4, TimeSpan.FromSeconds(10));

        await tcp.RebindAsync(newPort, default);

        tcp.BoundPort.Should().Be(newPort);

        await LoopbackSyslog.SendTcpNewlineAsync(newPort, 4, prefix: "after");
        await WaitForReceivedAsync(h, 8, TimeSpan.FromSeconds(10));

        await tcp.StopAsync(default);
        await h.DrainAsync();

        h.Stats.Snapshot().Total.Received.Should().Be(8);

        Func<Task> connectOld = async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, oldPort);
        };
        await connectOld.Should().ThrowAsync<SocketException>("the old TCP listen socket was closed by the rebind");
    }

    [Fact]
    public async Task Tcp_RebindAsync_PortAlreadyInUse_ThrowsAndLeavesOriginalListenerRunning()
    {
        int oldPort = LoopbackSyslog.FreeTcpPort();
        int busyPort = LoopbackSyslog.FreeTcpPort();
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, busyPort));
        blocker.Listen(1);

        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = oldPort;
        });

        TcpSyslogListener tcp = h.AddTcpListener();
        await tcp.StartAsync(default);
        h.StartPipeline();

        Func<Task> rebind = () => tcp.RebindAsync(busyPort, default);
        await rebind.Should().ThrowAsync<SocketException>();

        tcp.BoundPort.Should().Be(oldPort, "a failed rebind must not disturb the listener that was already working");

        await LoopbackSyslog.SendTcpNewlineAsync(oldPort, 2, prefix: "still-alive");
        await WaitForReceivedAsync(h, 2, TimeSpan.FromSeconds(10));
        h.Stats.Snapshot().Total.Received.Should().Be(2);

        await tcp.StopAsync(default);
        await h.DrainAsync();
    }
}
