using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

[Trait("Category", "Ingestion")]
public sealed class RateLimitTests
{
    private static RawFrame Frame(string ip, int i) => new(
        DateTimeOffset.UtcNow, ip, "udp:test", Protocol.Udp,
        System.Text.Encoding.UTF8.GetBytes($"<13>rl-{i}"), truncated: false);

    [Fact]
    public async Task DropBehaviour_DiscardsOverCeilingFrames_AndIncrementsTheCounter_NeverExceedingTheBudget()
    {
        var clock = new FakeTimeProvider();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configure: o =>
            {
                o.PerSourceRatePerSecond = 100;
                o.PerSourceBurstMultiplier = 1;             // capacity 100
                o.RateLimitBreachBehavior = RateLimitBreachBehavior.Drop;
                o.ThrottleDelay = TimeSpan.Zero;
            },
            timeProvider: clock);

        h.StartPipeline();

        for (int i = 0; i < 1_000; i++)
        {
            await h.Intake.AcceptAsync(Frame("192.0.2.50", i), default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(30));

        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.Total.Received.Should().Be(1_000);
        s.Total.DroppedRateLimited.Should().Be(900);
        s.Total.Committed.Should().Be(100);
        (await h.CommittedCountAsync()).Should().Be(100);
    }

    [Fact]
    public async Task ThrottleBehaviour_KeepsEveryFrame_AndCountsTheBreaches()
    {
        var clock = new FakeTimeProvider();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configure: o =>
            {
                o.PerSourceRatePerSecond = 100;
                o.PerSourceBurstMultiplier = 1;
                o.RateLimitBreachBehavior = RateLimitBreachBehavior.Throttle;
                o.ThrottleDelay = TimeSpan.Zero; // keep the test fast; behaviour under test is "no loss"
            },
            timeProvider: clock);

        h.StartPipeline();

        for (int i = 0; i < 500; i++)
        {
            await h.Intake.AcceptAsync(Frame("192.0.2.51", i), default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(30));

        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.Total.Dropped.Should().Be(0, "throttle never loses a message");
        s.Total.Throttled.Should().Be(400);
        s.Total.Committed.Should().Be(500);
        (await h.CommittedCountAsync()).Should().Be(500);
    }

    [Fact]
    public async Task QuarantineBehaviour_SurfacesTheSourceAndKeepsDroppingForTheCooldown()
    {
        var clock = new FakeTimeProvider();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configure: o =>
            {
                o.PerSourceRatePerSecond = 50;
                o.PerSourceBurstMultiplier = 1;
                o.RateLimitBreachBehavior = RateLimitBreachBehavior.Quarantine;
                o.QuarantineDuration = TimeSpan.FromMinutes(1);
                o.ThrottleDelay = TimeSpan.Zero;
            },
            timeProvider: clock);

        h.StartPipeline();

        for (int i = 0; i < 200; i++)
        {
            await h.Intake.AcceptAsync(Frame("198.51.100.77", i), default);
        }

        h.Stats.Snapshot().QuarantinedSources.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 50; i++)
        {
            await h.Intake.AcceptAsync(Frame("198.51.100.77", 1000 + i), default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(30));

        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.Total.DroppedRateLimited.Should().BeGreaterThan(150);
        s.Total.Committed.Should().Be(50, "only the initial burst before quarantine got through");
    }

    [Fact]
    public async Task RateLimiter_OneAbusiveSource_DoesNotStarveAWellBehavedOne()
    {
        var clock = new FakeTimeProvider();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configure: o =>
            {
                o.PerSourceRatePerSecond = 20;
                o.PerSourceBurstMultiplier = 1;
                o.RateLimitBreachBehavior = RateLimitBreachBehavior.Drop;
                o.ThrottleDelay = TimeSpan.Zero;
            },
            timeProvider: clock);

        h.StartPipeline();

        for (int i = 0; i < 1_000; i++)
        {
            await h.Intake.AcceptAsync(Frame("10.10.10.10", i), default);   // abusive
        }

        for (int i = 0; i < 20; i++)
        {
            await h.Intake.AcceptAsync(Frame("10.20.20.20", i), default);   // polite, within budget
        }

        await h.DrainAsync(TimeSpan.FromSeconds(30));

        IngestionStatsSnapshot s = h.Stats.Snapshot();
        s.BySource["10.20.20.20"].Committed.Should().Be(20);
        s.BySource["10.20.20.20"].Dropped.Should().Be(0);
    }
}
