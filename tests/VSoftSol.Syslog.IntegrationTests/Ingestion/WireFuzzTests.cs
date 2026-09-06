using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>
/// PHASE_02 "Fuzz the wire" / Security Validation "Malformed frame fuzzing". Hostile bytes
/// must never crash a listener, hang it, leak a socket, or trigger an unbounded allocation.
/// The default run uses a modest corpus; the full 1M-frame corpus is the Soak variant.
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class WireFuzzTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(20_000)]
    [Trait("Category", "Soak")]
    [InlineData(1_000_000)]
    public async Task HostileFrames_NeverCrashOrHangTheListeners_NoSocketLeak(int frames)
    {
        int udpPort = LoopbackSyslog.FreeUdpPort();
        int tcpPort = LoopbackSyslog.FreeTcpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = udpPort;
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = tcpPort;
            o.MaxMessageBytes = 64 * 1024;
            o.ChannelCapacity = 20_000;
        });

        UdpSyslogListener udp = h.AddUdpListener();
        TcpSyslogListener tcp = h.AddTcpListener();
        await udp.StartAsync(default);
        await tcp.StartAsync(default);
        h.StartPipeline();

        var rng = new Random(20260906);
        long handleBaseline = CurrentProcessHandles();

        await FuzzUdpAsync(udpPort, frames / 2, rng);
        await FuzzTcpAsync(tcpPort, frames / 2, rng);

        long committedBefore = h.Stats.Snapshot().Total.Committed;

        // Prove the listeners are still alive: well-formed messages still get through and
        // get committed. (This test is about "no crash / hang / leak", not throughput — so
        // it does not wait for the whole fuzz corpus to drain.)
        await LoopbackSyslog.SendUdpRawAsync(udpPort, [System.Text.Encoding.UTF8.GetBytes("<13>still alive udp one"),
            System.Text.Encoding.UTF8.GetBytes("<13>still alive udp two")]);
        await LoopbackSyslog.SendTcpNewlineAsync(tcpPort, 3, "still-alive-tcp");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline && h.Stats.Snapshot().Total.Committed < committedBefore + 5)
        {
            await Task.Delay(50);
        }

        await udp.StopAsync(default);
        await tcp.StopAsync(default);
        await h.HardStopPipelineAsync();

        IngestionStatsSnapshot end = h.Stats.Snapshot();
        end.Total.Committed.Should().BeGreaterThanOrEqualTo(committedBefore + 5, "the listeners survived the fuzz corpus and kept ingesting");
        end.Total.Received.Should().BeGreaterThan(frames / 4, "the fuzz frames were received, not silently dropped by a broken listener");

        long handlesAfter = CurrentProcessHandles();
        output.WriteLine($"fuzz {frames}: received={end.Total.Received} committed={end.Total.Committed} handles {handleBaseline}->{handlesAfter}");
        (handlesAfter - handleBaseline).Should().BeLessThan(2_000, "no socket/handle leak under the fuzz corpus");
    }

    private static async Task FuzzUdpAsync(int port, int count, Random rng)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        for (int i = 0; i < count; i++)
        {
            byte[] payload = (i % 7) switch
            {
                0 => [],
                1 => RandomBytes(rng, rng.Next(1, 64)),
                2 => RandomBytes(rng, 65_507),
                3 => new byte[rng.Next(1, 256)], // all NUL
                4 => System.Text.Encoding.UTF8.GetBytes($"<{rng.Next(0, 999)}>partial"),
                5 => [0xC0, 0x80, 0xFF, 0xFE, .. RandomBytes(rng, 16)], // invalid UTF-8
                _ => System.Text.Encoding.UTF8.GetBytes(new string('%', rng.Next(1, 40)) + "n%s%x"),
            };

            try
            {
                await client.SendToAsync(payload, SocketFlags.None, endpoint);
            }
            catch (SocketException)
            {
            }

            if (i % 512 == 0)
            {
                await Task.Delay(1);
            }
        }
    }

    private static async Task FuzzTcpAsync(int port, int count, Random rng)
    {
        int sent = 0;
        while (sent < count)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                await using NetworkStream stream = client.GetStream();

                int burst = Math.Min(count - sent, rng.Next(1, 200));
                for (int i = 0; i < burst; i++, sent++)
                {
                    byte[] chunk = (sent % 6) switch
                    {
                        0 => RandomBytes(rng, rng.Next(1, 300)),
                        1 => System.Text.Encoding.ASCII.GetBytes($"{rng.Next(1, 99)} <13>x"),          // maybe-truncated octet frame
                        2 => System.Text.Encoding.ASCII.GetBytes("999999999999999 flood"),             // absurd length -> connection dropped
                        3 => System.Text.Encoding.ASCII.GetBytes("<13>a\n<13>b\n5 <13>x"),              // mixed framing on one connection
                        4 => new byte[rng.Next(1, 64)],                                                 // NUL run
                        _ => System.Text.Encoding.ASCII.GetBytes("<13>" + new string('Z', rng.Next(1, 200)) + "\n"),
                    };

                    await stream.WriteAsync(chunk);
                }

                await stream.FlushAsync();
            }
            catch (SocketException)
            {
            }
            catch (IOException)
            {
            }

            if (sent % 2048 < 200)
            {
                await Task.Delay(1);
            }
        }
    }

    private static byte[] RandomBytes(Random rng, int n)
    {
        byte[] b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    private static long CurrentProcessHandles()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.HandleCount;
    }
}
