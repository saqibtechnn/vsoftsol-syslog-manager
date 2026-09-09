using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Rules;
using VSoftSol.Syslog.UnitTests.Conditions;
using VSoftSol.Syslog.UnitTests.TestSupport;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Rules.RuleTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Rules;

public sealed class RuleRuntimeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static (RuleSet Set, RuleRuntime Runtime, ManualClock Clock) Harness(
        RuleRuntimeOptions? options = null, params RuleDefinition[] rules)
    {
        var clock = new ManualClock();
        RuleSet set = RuleSetTests.Build(rules);
        var runtime = new RuleRuntime(clock, options ?? new RuleRuntimeOptions { GlobalActionsPerMinute = 0 });
        return (set, runtime, clock);
    }

    private static RuleOutcome Run(RuleSet set, RuleRuntime runtime, SyslogEvent e) =>
        runtime.Apply(set.Match(e, [], Noon), e);

    [Fact]
    public void Apply_SuppressAction_HaltsFurtherRules()
    {
        var (set, runtime, _) = Harness(
            rules: new[]
            {
                Rule("first", 1, null, Notify("a"), Suppress()),
                Rule("second", 2, null, Notify("b")),
            });

        RuleOutcome outcome = Run(set, runtime, ConditionTestBuilders.Event());

        outcome.Suppressed.Should().BeTrue();
        outcome.Dispatches.Select(d => ((RaiseNotificationAction)d.Action).Title).Should().Equal("a");
    }

    [Fact]
    public void Apply_InlineActions_ProduceTagsAndStreamIds_NotDispatches()
    {
        var (set, runtime, _) = Harness(
            rules: new[] { Rule("t", null, Tag("high-value"), Route(42)) });

        RuleOutcome outcome = Run(set, runtime, ConditionTestBuilders.Event());

        outcome.Tags.Should().ContainSingle(f => f.Name == "tag" && f.Value == "high-value");
        outcome.ExtraStreamIds.Should().Equal(42L);
        outcome.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public void Apply_RateLimit_ProducesExactlyTheConfiguredNumberOfDispatches()
    {
        var throttle = new ActionThrottle(MaxPerWindow: 5, WindowSeconds: 60, CooldownSeconds: 0);
        var (set, runtime, _) = Harness(
            rules: new[] { Rule("rl", null, Notify("x", throttle)) });

        int dispatched = 0;
        int limited = 0;
        for (int i = 0; i < 1000; i++)
        {
            RuleOutcome o = Run(set, runtime, ConditionTestBuilders.Event());
            dispatched += o.Dispatches.Count;
            limited += o.RateLimitedCount;
        }

        dispatched.Should().Be(5);
        limited.Should().Be(995);
    }

    [Fact]
    public void Apply_RateLimit_WindowSlides_AllowsMoreAfterTheWindow()
    {
        var throttle = new ActionThrottle(2, WindowSeconds: 10, CooldownSeconds: 0);
        var (set, runtime, clock) = Harness(rules: new[] { Rule("rl", null, Notify("x", throttle)) });

        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().ContainSingle();
        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().ContainSingle();
        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(11));
        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().ContainSingle();
    }

    [Fact]
    public void Apply_Cooldown_BlocksUntilTheGapElapses()
    {
        var throttle = new ActionThrottle(0, 0, CooldownSeconds: 30);
        var (set, runtime, clock) = Harness(rules: new[] { Rule("cd", null, Notify("x", throttle)) });

        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().ContainSingle();
        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(15));
        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(16));
        Run(set, runtime, ConditionTestBuilders.Event()).Dispatches.Should().ContainSingle();
    }

    [Fact]
    public void Apply_Escalation_SwapsToTheEscalationListOnceWhenTheThresholdIsHitInWindow()
    {
        var rule = Rule("hw", All(Cmp("message", ConditionOperator.Contains, "fan")), Notify("normal"));
        rule.Escalation = new EscalationPolicy
        {
            Threshold = 3,
            WindowSeconds = 60,
            EscalationActions = [Notify("ESCALATED")],
        };
        var (set, runtime, clock) = Harness(rules: new[] { rule });
        SyslogEvent e = ConditionTestBuilders.Event(message: "fan failure");

        Run(set, runtime, e).Dispatches.Single().WasEscalation.Should().BeFalse();
        Run(set, runtime, e).Dispatches.Single().WasEscalation.Should().BeFalse();

        // 3rd match in the window → escalation list, once
        PendingDispatch third = Run(set, runtime, e).Dispatches.Single();
        third.WasEscalation.Should().BeTrue();
        ((RaiseNotificationAction)third.Action).Title.Should().Be("ESCALATED");

        // 4th match still in window → latch closed → back to normal
        Run(set, runtime, e).Dispatches.Single().WasEscalation.Should().BeFalse();
    }

    [Fact]
    public void Apply_GlobalBudget_CollapsesExcessDispatchesIntoASingleSummary()
    {
        var options = new RuleRuntimeOptions { GlobalActionsPerMinute = 10 };
        var (set, runtime, _) = Harness(options, rules: new[] { Rule("noisy", null, Notify("x")) });

        int dispatched = 0;
        int summaries = 0;
        int collapsed = 0;
        for (int i = 0; i < 100; i++)
        {
            RuleOutcome o = Run(set, runtime, ConditionTestBuilders.Event());
            dispatched += o.Dispatches.Count;
            if (o.Storm is { } s)
            {
                summaries++;
                collapsed += s.TotalCollapsed;
            }
        }

        dispatched.Should().Be(10);
        summaries.Should().Be(1, "one summary per minute");
        collapsed.Should().BeGreaterThan(0);
    }
}
