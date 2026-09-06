using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>
/// PHASE_02 chaos suite. Each scenario is repeated to catch races (fast run: 3×; the
/// Soak variant: 10×, matching the phase's "10 runs × scenario" matrix). The hard-kill
/// scenario lives in <see cref="KillRecoveryTests"/>; connection floods and half-open
/// sockets are in <see cref="IngestionSecurityTests"/> and <see cref="TcpIngestionTests"/>.
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class ChaosTests(ITestOutputHelper output)
{
    private static RawFrame Frame(int i) => new(
        DateTimeOffset.UtcNow, "198.51.100.20", "udp:test", Protocol.Udp,
        System.Text.Encoding.UTF8.GetBytes($"<13>chaos-{i}"), truncated: false);

    [Theory]
    [InlineData(3)]
    [Trait("Category", "Soak")]
    [InlineData(10)]
    public async Task RepositoryStalledMidIngest_SpillEngagesAndDrainsCleanly_EveryRun(int runs)
    {
        for (int run = 1; run <= runs; run++)
        {
            StallableLogRepository? stall = null;
            await using IngestionHarness h = await IngestionHarness.CreateAsync(
                configure: o => { o.ChannelCapacity = 800; o.BatchSize = 200; },
                decorateRepository: inner => stall = new StallableLogRepository(inner));
            h.StartPipeline();

            const int total = 15_000;
            stall!.Stall();
            Task producer = Task.Run(async () =>
            {
                for (int i = 0; i < total; i++)
                {
                    await h.Intake.AcceptAsync(Frame(i), default);
                }
            });

            await Task.Delay(200);
            h.Spill.PendingFrameCount.Should().BeGreaterThan(0, "run {0}: the stall must push to disk", run);
            stall.Release();
            await producer;
            await h.DrainAsync(TimeSpan.FromSeconds(90));

            (await h.CommittedCountAsync()).Should().Be(total, "run {0}", run);
            IngestionStatsSnapshot s = h.Stats.Snapshot();
            s.Total.Dropped.Should().Be(0, "run {0}", run);
            s.InFlight.Should().Be(0, "run {0}", run);
            h.Spill.IsEmpty.Should().BeTrue("run {0}", run);
            output.WriteLine($"run {run}: {total} committed, spill drained, ledger balanced");
        }
    }

    [Theory]
    [InlineData(3)]
    [Trait("Category", "Soak")]
    [InlineData(10)]
    public async Task DiskFullDuringSpill_DegradesGracefullyWithAnAlert_NeverCorrupts_EveryRun(int runs)
    {
        for (int run = 1; run <= runs; run++)
        {
            StallableLogRepository? stall = null;
            await using IngestionHarness h = await IngestionHarness.CreateAsync(
                configure: o =>
                {
                    o.ChannelCapacity = 500;
                    o.BatchSize = 200;
                    o.SpillMaxBytes = 16 * 1024 * 1024; // small cap so it fills
                    o.SpillSegmentBytes = 2 * 1024 * 1024;
                },
                decorateRepository: inner => stall = new StallableLogRepository(inner));
            h.StartPipeline();

            stall!.Stall();
            int accepted = 0;
            byte[] big = new byte[50_000];
            for (int i = 0; i < 2_000; i++)
            {
                await h.Intake.AcceptAsync(
                    new RawFrame(DateTimeOffset.UtcNow, "198.51.100.20", "udp:test", Protocol.Udp, big, false), default);
                accepted++;
            }

            IngestionStatsSnapshot mid = h.Stats.Snapshot();
            mid.Total.DroppedSpillFull.Should().BeGreaterThan(0, "run {0}: the disk cap must bite and be counted", run);

            stall.Release();
            await h.DrainAsync(TimeSpan.FromSeconds(90));

            IngestionStatsSnapshot end = h.Stats.Snapshot();
            long committed = await h.CommittedCountAsync();

            // Every frame is accounted for: committed + dropped-with-counter, nothing silently vanished.
            (end.Total.Committed + end.Total.Dropped).Should().Be(accepted, "run {0}: the ledger balances", run);
            committed.Should().Be(end.Total.Committed);
            end.Total.Failed.Should().Be(0, "run {0}", run);

            await using var conn = await h.Db.Factory.OpenAsync(default);
            await using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            (await check.ExecuteScalarAsync())!.ToString().Should().Be("ok", "run {0}: never corrupts", run);
            output.WriteLine($"run {run}: accepted={accepted} committed={end.Total.Committed} droppedSpillFull={end.Total.DroppedSpillFull}");
        }
    }
}
