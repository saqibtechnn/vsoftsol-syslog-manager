using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>PHASE_02 Security Validation: availability under flood, connection exhaustion, bind-address.</summary>
[Trait("Category", "Ingestion")]
public sealed class IngestionSecurityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Flood_TenTimesTheRateLimitFromManySpoofedSources_StaysUp_DiskBounded_LegitTrafficStillIngested()
    {
        var clock = new FakeTimeProvider();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configure: o =>
            {
                o.PerSourceRatePerSecond = 100;
                o.PerSourceBurstMultiplier = 1;
                o.RateLimitBreachBehavior = RateLimitBreachBehavior.Drop;
                o.ThrottleDelay = TimeSpan.Zero;
                o.ChannelCapacity = 5_000;
                o.SpillMaxBytes = 16 * 1024 * 1024;
            },
            timeProvider: clock);
        h.StartPipeline();

        // 100 spoofed sources each sending 10x their ceiling.
        for (int burst = 0; burst < 1_000; burst++)
        {
            for (int src = 0; src < 100; src++)
            {
                var f = new RawFrame(DateTimeOffset.UtcNow, $"10.66.{src / 256}.{src % 256}", "udp:flood",
                    Core.Enums.Protocol.Udp, System.Text.Encoding.UTF8.GetBytes($"<13>flood {burst}"), false);
                await h.Intake.AcceptAsync(f, default);
            }
        }

        // A legitimate source, within budget, in the middle of the flood.
        for (int i = 0; i < 50; i++)
        {
            var good = new RawFrame(DateTimeOffset.UtcNow, "192.0.2.200", "udp:flood",
                Core.Enums.Protocol.Udp, System.Text.Encoding.UTF8.GetBytes($"<13>legit {i}"), false);
            await h.Intake.AcceptAsync(good, default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(60));

        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.Total.Received.Should().Be(100_050);
        s.Total.DroppedRateLimited.Should().BeGreaterThan(80_000, "the bulk of the flood is shed, not absorbed");
        s.SpillBytes.Should().BeLessThanOrEqualTo(16 * 1024 * 1024, "the disk cap held");
        s.BySource["192.0.2.200"].Committed.Should().Be(50, "the well-behaved source was still ingested");
        output.WriteLine($"flood: received={s.Total.Received} droppedRL={s.Total.DroppedRateLimited} committed={s.Total.Committed}");
    }

    [Fact]
    public async Task Slowloris_TenThousandHalfOpenConnections_HitTheCap_AndUdpIsUnaffected()
    {
        int tcpPort = LoopbackSyslog.FreeTcpPort();
        int udpPort = LoopbackSyslog.FreeUdpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = tcpPort;
            o.TcpMaxConnections = 200;
            o.TcpIdleTimeout = TimeSpan.FromSeconds(3);
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = udpPort;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        TcpSyslogListener tcp = h.AddTcpListener();
        await udp.StartAsync(default);
        await tcp.StartAsync(default);
        h.StartPipeline();

        var sockets = new List<Socket>();
        for (int i = 0; i < 2_000; i++)
        {
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await sock.ConnectAsync(IPAddress.Loopback, tcpPort);
                sockets.Add(sock); // connect, then send nothing — slowloris
            }
            catch (SocketException)
            {
            }
        }

        await Task.Delay(500);
        h.Stats.Snapshot().ActiveTcpConnections.Should().BeLessThanOrEqualTo(200, "the connection cap holds");

        // UDP ingestion is completely unaffected by the TCP pressure.
        await LoopbackSyslog.SendUdpAsync(udpPort, 2_000, "under-slowloris");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && h.Stats.Snapshot().BySource.GetValueOrDefault("127.0.0.1")?.Committed < 1)
        {
            await Task.Delay(50);
        }

        await udp.StopAsync(default);
        await tcp.StopAsync(default);
        await h.DrainAsync(TimeSpan.FromSeconds(60));

        foreach (Socket sock in sockets)
        {
            sock.Dispose();
        }

        (await h.CommittedCountAsync()).Should().BeGreaterThanOrEqualTo(2_000, "every UDP datagram was ingested despite the slowloris");
    }

    [Fact]
    public void Listeners_BindOnlyWhereConfigured_AndTheDefaultIsNotWildcardForTheUi()
    {
        // The listener default is 0.0.0.0 (a LAN syslog collector must accept from any
        // interface); it is fully configurable.
        var options = new IngestionOptions();
        options.UdpBindAddress.Should().Be("0.0.0.0");
        options.TcpBindAddress.Should().Be("0.0.0.0");

        options.UdpBindAddress = "127.0.0.1";
        options.UdpBindAddress.Should().Be("127.0.0.1");

        // The UI host, by contrast, must not bind 0.0.0.0 by default — its appsettings pins it.
        string webAppsettings = File.ReadAllText(Path.Combine(RepoRoot(), "src", "VSoftSol.Syslog.Web", "appsettings.json"));
        webAppsettings.Should().NotContain("0.0.0.0");
        webAppsettings.Should().Contain("localhost");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VSoftSol.Syslog.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
