using FluentAssertions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.UnitTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Ingestion;

public sealed class PerSourceRateLimiterTests
{
    private static PerSourceRateLimiter Build(ManualClock clock, Action<IngestionOptions> configure)
    {
        var options = new IngestionOptions();
        configure(options);
        return new PerSourceRateLimiter(Options.Create(options), clock);
    }

    [Fact]
    public void Check_WhenRateLimitingIsDisabled_AlwaysAllows()
    {
        var clock = new ManualClock();
        PerSourceRateLimiter limiter = Build(clock, o => o.PerSourceRatePerSecond = 0);

        for (int i = 0; i < 10_000; i++)
        {
            limiter.Check("10.0.0.1").Should().Be(PerSourceRateLimiter.Decision.Allow);
        }
    }

    [Fact]
    public void Check_ThrottleBehaviour_ReturnsThrottleAfterTheBurstIsSpent_ThenRecovers()
    {
        var clock = new ManualClock();
        PerSourceRateLimiter limiter = Build(clock, o =>
        {
            o.PerSourceRatePerSecond = 100;
            o.PerSourceBurstMultiplier = 2; // capacity 200
            o.RateLimitBreachBehavior = RateLimitBreachBehavior.Throttle;
        });

        int allowed = 0;
        for (int i = 0; i < 200; i++)
        {
            if (limiter.Check("10.0.0.2") == PerSourceRateLimiter.Decision.Allow)
            {
                allowed++;
            }
        }

        allowed.Should().Be(200, "the full burst is admitted");
        limiter.Check("10.0.0.2").Should().Be(PerSourceRateLimiter.Decision.Throttle);

        clock.Advance(TimeSpan.FromSeconds(1)); // refills 100 tokens
        limiter.Check("10.0.0.2").Should().Be(PerSourceRateLimiter.Decision.Allow);
    }

    [Fact]
    public void Check_DropBehaviour_ReturnsDropWhileOverBudget()
    {
        var clock = new ManualClock();
        PerSourceRateLimiter limiter = Build(clock, o =>
        {
            o.PerSourceRatePerSecond = 10;
            o.PerSourceBurstMultiplier = 1;
            o.RateLimitBreachBehavior = RateLimitBreachBehavior.Drop;
        });

        for (int i = 0; i < 10; i++)
        {
            limiter.Check("10.0.0.3").Should().Be(PerSourceRateLimiter.Decision.Allow);
        }

        limiter.Check("10.0.0.3").Should().Be(PerSourceRateLimiter.Decision.Drop);
    }

    [Fact]
    public void Check_QuarantineBehaviour_KeepsDroppingForTheCooldown_ThenReleases()
    {
        var clock = new ManualClock();
        PerSourceRateLimiter limiter = Build(clock, o =>
        {
            o.PerSourceRatePerSecond = 5;
            o.PerSourceBurstMultiplier = 1;
            o.RateLimitBreachBehavior = RateLimitBreachBehavior.Quarantine;
            o.QuarantineDuration = TimeSpan.FromSeconds(30);
        });

        for (int i = 0; i < 5; i++)
        {
            limiter.Check("10.0.0.4");
        }

        limiter.Check("10.0.0.4").Should().Be(PerSourceRateLimiter.Decision.Drop);
        limiter.QuarantinedCount.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(5)); // tokens refilled, but still quarantined
        limiter.Check("10.0.0.4").Should().Be(PerSourceRateLimiter.Decision.Drop);

        clock.Advance(TimeSpan.FromSeconds(30));
        limiter.Check("10.0.0.4").Should().Be(PerSourceRateLimiter.Decision.Allow);
        limiter.QuarantinedCount.Should().Be(0);
    }

    [Fact]
    public void Check_OneNoisySource_DoesNotAffectAnother()
    {
        var clock = new ManualClock();
        PerSourceRateLimiter limiter = Build(clock, o =>
        {
            o.PerSourceRatePerSecond = 5;
            o.PerSourceBurstMultiplier = 1;
            o.RateLimitBreachBehavior = RateLimitBreachBehavior.Drop;
        });

        for (int i = 0; i < 50; i++)
        {
            limiter.Check("10.0.0.5");
        }

        limiter.Check("10.0.0.5").Should().Be(PerSourceRateLimiter.Decision.Drop);
        limiter.Check("10.0.0.6").Should().Be(PerSourceRateLimiter.Decision.Allow);
    }
}
