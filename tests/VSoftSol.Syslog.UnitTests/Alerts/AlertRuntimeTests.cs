using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Alerts;
using VSoftSol.Syslog.UnitTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Alerts;

/// <summary>
/// PHASE_08 security — the alert-action budget. An attacker who can generate log events must
/// not be able to weaponise alerting against the admin's inbox: the global per-minute budget
/// collapses the overflow to a single summary, and per-action rate limits / cool-downs hold.
/// </summary>
public sealed class AlertRuntimeTests
{
    private static RaiseNotificationAction Notify(ActionThrottle? throttle = null) =>
        new() { Title = "x", Throttle = throttle ?? ActionThrottle.None };

    [Fact]
    public void Reserve_UpToTheGlobalBudget_ThenCollapses()
    {
        var clock = new ManualClock();
        var runtime = new AlertRuntime(clock, new AlertRuntimeOptions { GlobalActionsPerMinute = 3 });

        var decisions = Enumerable.Range(0, 6).Select(_ => runtime.Reserve(1, Notify())).ToList();

        decisions.Take(3).Should().OnlyContain(d => d == AlertDispatchDecision.Allow);
        decisions.Skip(3).Should().OnlyContain(d => d == AlertDispatchDecision.StormCollapsed);
    }

    [Fact]
    public void GlobalBudget_RefillsAfterAMinute()
    {
        var clock = new ManualClock();
        var runtime = new AlertRuntime(clock, new AlertRuntimeOptions { GlobalActionsPerMinute = 2 });

        runtime.Reserve(1, Notify());
        runtime.Reserve(1, Notify());
        runtime.Reserve(1, Notify()).Should().Be(AlertDispatchDecision.StormCollapsed);

        clock.Advance(TimeSpan.FromMinutes(1));
        runtime.Reserve(1, Notify()).Should().Be(AlertDispatchDecision.Allow);
    }

    [Fact]
    public void Reserve_Cooldown_BlocksUntilTheGapElapses()
    {
        var clock = new ManualClock();
        var runtime = new AlertRuntime(clock, new AlertRuntimeOptions { GlobalActionsPerMinute = 0 });
        var action = Notify(new ActionThrottle(0, 0, CooldownSeconds: 300));

        runtime.Reserve(1, action).Should().Be(AlertDispatchDecision.Allow);
        runtime.Reserve(1, action).Should().Be(AlertDispatchDecision.RateLimited);

        clock.Advance(TimeSpan.FromSeconds(299));
        runtime.Reserve(1, action).Should().Be(AlertDispatchDecision.RateLimited);

        clock.Advance(TimeSpan.FromSeconds(2));
        runtime.Reserve(1, action).Should().Be(AlertDispatchDecision.Allow);
    }

    [Fact]
    public void Reserve_RateLimit_AllowsExactlyNPerWindow()
    {
        var clock = new ManualClock();
        var runtime = new AlertRuntime(clock, new AlertRuntimeOptions { GlobalActionsPerMinute = 0 });
        var action = Notify(new ActionThrottle(MaxPerWindow: 2, WindowSeconds: 60, CooldownSeconds: 0));

        int allowed = Enumerable.Range(0, 10).Count(_ => runtime.Reserve(1, action) == AlertDispatchDecision.Allow);
        allowed.Should().Be(2);

        clock.Advance(TimeSpan.FromSeconds(61));
        runtime.Reserve(1, action).Should().Be(AlertDispatchDecision.Allow);
    }

    [Fact]
    public void TakeStormSummary_AggregatesByKind_AndFiresAtMostOncePerMinute()
    {
        var clock = new ManualClock();
        var runtime = new AlertRuntime(clock, new AlertRuntimeOptions { GlobalActionsPerMinute = 1 });

        runtime.Reserve(1, Notify());                       // allowed
        for (int i = 0; i < 5; i++)
        {
            runtime.Reserve(1, Notify());                   // collapsed
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        AlertStormSummary? summary = runtime.TakeStormSummary();
        summary.Should().NotBeNull();
        summary!.TotalCollapsed.Should().Be(5);
        summary.ByKind["Raise notification"].Should().Be(5);

        runtime.TakeStormSummary().Should().BeNull("the summary itself must not become the flood");
    }

    [Fact]
    public void TakeStormSummary_IsNull_WhenNothingWasCollapsed()
    {
        var runtime = new AlertRuntime(new ManualClock(), new AlertRuntimeOptions { GlobalActionsPerMinute = 100 });
        runtime.Reserve(1, Notify());
        runtime.TakeStormSummary().Should().BeNull();
    }
}
