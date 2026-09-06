using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

[Trait("Category", "Ingestion")]
public sealed class UdpIngestionTests
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
    public async Task Udp_OneHundredThousandDatagrams_AllCommitted_LedgerBalances()
    {
        const int count = 100_000;
        int port = LoopbackSyslog.FreeUdpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = port;
            o.ChannelCapacity = 20_000;
        });

        UdpSyslogListener udp = h.AddUdpListener();
        await udp.StartAsync(default);
        h.StartPipeline();

        await LoopbackSyslog.SendUdpAsync(port, count);
        await WaitForReceivedAsync(h, count, TimeSpan.FromSeconds(60));
        await udp.StopAsync(default);
        await h.DrainAsync();

        long committed = await h.CommittedCountAsync();
        IngestionStatsSnapshot s = h.Stats.Snapshot();

        s.Total.Received.Should().Be(count, "loopback UDP is paced so nothing is lost at the socket");
        committed.Should().Be(count);
        s.Total.Committed.Should().Be(count);
        s.Total.Dropped.Should().Be(0);
        s.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Udp_ZeroLengthDatagram_IsCountedAndDoesNotCrashTheListener()
    {
        int port = LoopbackSyslog.FreeUdpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = port;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        await udp.StartAsync(default);
        h.StartPipeline();

        await LoopbackSyslog.SendUdpRawAsync(port, [[], [], System.Text.Encoding.UTF8.GetBytes("<13>real message")]);
        await WaitForReceivedAsync(h, 3, TimeSpan.FromSeconds(10));
        await udp.StopAsync(default);
        await h.DrainAsync();

        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.Total.Received.Should().Be(3);
        s.Total.DroppedEmpty.Should().Be(2);
        (await h.CommittedCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Udp_OversizedDatagram_IsStoredTruncated_NeverDropped()
    {
        int port = LoopbackSyslog.FreeUdpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = port;
            o.MaxMessageBytes = 2_048;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        await udp.StartAsync(default);
        h.StartPipeline();

        byte[] huge = new byte[60_000];
        Array.Fill(huge, (byte)'A');
        await LoopbackSyslog.SendUdpRawAsync(port, [huge]);
        await WaitForReceivedAsync(h, 1, TimeSpan.FromSeconds(10));
        await udp.StopAsync(default);
        await h.DrainAsync();

        (await h.CommittedCountAsync()).Should().Be(1);
        SyslogEvent stored = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        stored.RawMessage.Length.Should().Be(2_048);
        stored.Fields.Should().Contain(f => f.Name == "truncated" && f.Value == "true");
        stored.ParseStatus.Should().Be(ParseStatus.Raw);
    }

    [Fact]
    public async Task Udp_LargeButLegalDatagram_IsStoredWhole()
    {
        int port = LoopbackSyslog.FreeUdpPort();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = port;
            o.MaxMessageBytes = 64 * 1024;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        await udp.StartAsync(default);
        h.StartPipeline();

        byte[] big = new byte[60_000];
        Array.Fill(big, (byte)'Z');
        await LoopbackSyslog.SendUdpRawAsync(port, [big]);
        await WaitForReceivedAsync(h, 1, TimeSpan.FromSeconds(10));
        await udp.StopAsync(default);
        await h.DrainAsync();

        SyslogEvent stored = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        stored.RawMessage.Length.Should().Be(60_000);
    }
}
