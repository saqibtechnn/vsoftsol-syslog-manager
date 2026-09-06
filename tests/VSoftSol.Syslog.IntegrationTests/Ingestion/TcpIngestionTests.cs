using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

[Trait("Category", "Ingestion")]
public sealed class TcpIngestionTests
{
    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static async Task<IngestionHarness> StartHarnessAsync(int port, Action<IngestionOptions>? tweak = null)
    {
        IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = port;
            o.ChannelCapacity = 20_000;
            tweak?.Invoke(o);
        });
        TcpSyslogListener tcp = h.AddTcpListener();
        await tcp.StartAsync(default);
        h.StartPipeline();
        return h;
    }

    [Fact]
    public async Task Tcp_OneHundredThousandNewlineFramedMessages_AllCommitted()
    {
        const int count = 100_000;
        int port = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await StartHarnessAsync(port);

        await LoopbackSyslog.SendTcpNewlineAsync(port, count);
        await WaitAsync(() => h.Stats.Snapshot().Total.Received >= count, TimeSpan.FromSeconds(120));
        await h.DrainAsync(TimeSpan.FromSeconds(180));

        (await h.CommittedCountAsync()).Should().Be(count);
        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.Total.Received.Should().Be(count);
        s.Total.Dropped.Should().Be(0);
        s.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Tcp_OneHundredThousandOctetCountedMessages_AllCommitted()
    {
        const int count = 100_000;
        int port = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await StartHarnessAsync(port);

        await LoopbackSyslog.SendTcpOctetCountedAsync(port, count);
        await WaitAsync(() => h.Stats.Snapshot().Total.Received >= count, TimeSpan.FromSeconds(120));
        await h.DrainAsync(TimeSpan.FromSeconds(180));

        (await h.CommittedCountAsync()).Should().Be(count);
        h.Stats.Snapshot().Total.Dropped.Should().Be(0);
    }

    [Fact]
    public async Task Tcp_FramesSplitAcrossArbitraryTcpSegments_AreReassembled()
    {
        int port = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await StartHarnessAsync(port);

        byte[] wire = Encoding.UTF8.GetBytes("<13>alpha\n<13>bravo\n<13>charlie\n");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using NetworkStream ns = client.GetStream();
            foreach (byte b in wire)
            {
                await ns.WriteAsync(new[] { b });
                await ns.FlushAsync();
            }
        }

        await WaitAsync(() => h.Stats.Snapshot().Total.Received >= 3, TimeSpan.FromSeconds(10));
        await h.DrainAsync();
        (await h.CommittedCountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Tcp_ConnectionCap_RejectsExcessConnections_ButKeepsIngesting()
    {
        int port = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await StartHarnessAsync(port, o => o.TcpMaxConnections = 5);

        var held = new List<TcpClient>();
        for (int i = 0; i < 20; i++)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            held.Add(c);
        }

        await Task.Delay(300);
        h.Stats.Snapshot().ActiveTcpConnections.Should().BeLessThanOrEqualTo(5);

        // A fresh connection still can't get a slot, but once we free some it works.
        foreach (TcpClient c in held)
        {
            c.Dispose();
        }

        await Task.Delay(300);
        await LoopbackSyslog.SendTcpNewlineAsync(port, 100);
        await WaitAsync(() => h.Stats.Snapshot().Total.Received >= 100, TimeSpan.FromSeconds(10));
        await h.DrainAsync();
        (await h.CommittedCountAsync()).Should().Be(100);
    }

    [Fact]
    public async Task Tcp_IdleConnection_IsClosedAfterTheIdleTimeout_WithoutAffectingOthers()
    {
        int port = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await StartHarnessAsync(port, o => o.TcpIdleTimeout = TimeSpan.FromSeconds(5));

        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(300);
        h.Stats.Snapshot().ActiveTcpConnections.Should().Be(1);

        await WaitAsync(() => h.Stats.Snapshot().ActiveTcpConnections == 0, TimeSpan.FromSeconds(12));
        h.Stats.Snapshot().ActiveTcpConnections.Should().Be(0);

        await LoopbackSyslog.SendTcpNewlineAsync(port, 50);
        await WaitAsync(() => h.Stats.Snapshot().Total.Received >= 50, TimeSpan.FromSeconds(10));
        await h.DrainAsync();
        (await h.CommittedCountAsync()).Should().Be(50);
    }

    [Fact]
    public async Task Tcp_AbruptClientReset_DoesNotLeakOrCrash()
    {
        int port = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await StartHarnessAsync(port);

        for (int i = 0; i < 50; i++)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            await using NetworkStream ns = c.GetStream();
            await ns.WriteAsync(Encoding.UTF8.GetBytes("<13>partial-no-newline"));
            c.LingerState = new LingerOption(true, 0); // force RST on close
        }

        await Task.Delay(500);
        await LoopbackSyslog.SendTcpNewlineAsync(port, 100);
        await WaitAsync(() => h.Stats.Snapshot().Total.Received >= 100, TimeSpan.FromSeconds(10));
        await h.DrainAsync();

        h.Stats.Snapshot().ActiveTcpConnections.Should().Be(0);
        (await h.CommittedCountAsync()).Should().BeGreaterThanOrEqualTo(100);
    }
}
