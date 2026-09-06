using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

[Trait("Category", "Ingestion")]
public sealed class BackpressureSpillTests
{
    private static RawFrame Frame(int i) => new(
        DateTimeOffset.UtcNow, "203.0.113.4", "udp:test", Protocol.Udp,
        System.Text.Encoding.UTF8.GetBytes($"<13>backpressure-{i}"), truncated: false);

    [Fact]
    public async Task WhenTheRepositoryStalls_FramesSpillToDisk_AndNothingIsLostWhenItRecovers()
    {
        StallableLogRepository? stall = null;
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configure: o =>
            {
                o.ChannelCapacity = 1_000;
                o.BatchSize = 200;
            },
            decorateRepository: inner => stall = new StallableLogRepository(inner));

        h.StartPipeline();
        stall!.Stall();

        const int total = 25_000;
        for (int i = 0; i < total; i++)
        {
            await h.Intake.AcceptAsync(Frame(i), default);
        }

        IngestionStatsSnapshot mid = h.Stats.Snapshot();
        mid.Total.Spilled.Should().BeGreaterThan(0, "a stalled writer must push the overflow to disk");
        h.Spill.PendingFrameCount.Should().BeGreaterThan(0);
        mid.Total.Dropped.Should().Be(0);

        stall.Release();
        await h.DrainAsync(TimeSpan.FromSeconds(90));

        long committed = await h.CommittedCountAsync();
        IngestionStatsSnapshot end = h.Stats.Snapshot();

        committed.Should().Be(total);
        end.Total.Received.Should().Be(total);
        end.Total.Committed.Should().Be(total);
        end.Total.Dropped.Should().Be(0);
        end.InFlight.Should().Be(0);
        h.Spill.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task WhenTheRepositoryThrowsTransiently_FramesAreRetriedNotLost()
    {
        StallableLogRepository? stall = null;
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            decorateRepository: inner => stall = new StallableLogRepository(inner));

        h.StartPipeline();
        stall!.FailNext(3);

        for (int i = 0; i < 1_000; i++)
        {
            await h.Intake.AcceptAsync(Frame(i), default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(60));

        (await h.CommittedCountAsync()).Should().Be(1_000);
        h.Stats.Snapshot().InFlight.Should().Be(0);
    }
}
