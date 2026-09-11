using FluentAssertions;
using VSoftSol.Syslog.Core.SelfMonitoring;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.SelfMonitoring;

[Trait("Category", "SelfMonitoring")]
public sealed class SelfMonitoringEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SelfMonitoringThresholds Thresholds = new();

    private static SelfMonitoringSnapshot Healthy(DateTimeOffset at) =>
        new(at, DiskFreeBytes: 50_000_000_000, DropCounterDelta: 0, QueueDepthPercent: 10, ListenersDown: [], ArchiveVerificationFailures: 0);

    [Fact]
    public void Evaluate_AllHealthy_ProducesNoTransitions()
    {
        (_, IReadOnlyList<SelfMonitoringTransition> transitions) =
            SelfMonitoringEvaluator.Evaluate(Healthy(T0), Thresholds, SelfMonitoringState.Empty);

        transitions.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_DiskBelowThreshold_FiresImmediately_NoSustainRequired()
    {
        SelfMonitoringSnapshot snap = Healthy(T0) with { DiskFreeBytes = 1_000_000_000 };

        (_, IReadOnlyList<SelfMonitoringTransition> transitions) =
            SelfMonitoringEvaluator.Evaluate(snap, Thresholds, SelfMonitoringState.Empty);

        transitions.Should().ContainSingle(t => t.Metric == SelfMonitoringMetric.DiskFree && t.Kind == SelfMonitoringTransitionKind.Started);
    }

    [Fact]
    public void Evaluate_AnyDrop_FiresImmediately()
    {
        SelfMonitoringSnapshot snap = Healthy(T0) with { DropCounterDelta = 1 };

        (_, IReadOnlyList<SelfMonitoringTransition> transitions) =
            SelfMonitoringEvaluator.Evaluate(snap, Thresholds, SelfMonitoringState.Empty);

        transitions.Should().ContainSingle(t => t.Metric == SelfMonitoringMetric.DropCounter && t.Kind == SelfMonitoringTransitionKind.Started);
    }

    [Fact]
    public void Evaluate_QueueDepthHigh_DoesNotFireUntilSustainedForTheThreshold()
    {
        SelfMonitoringSnapshot breach = Healthy(T0) with { QueueDepthPercent = 95 };
        SelfMonitoringState state = SelfMonitoringState.Empty;

        (state, IReadOnlyList<SelfMonitoringTransition> t1) = SelfMonitoringEvaluator.Evaluate(breach, Thresholds, state);
        t1.Should().BeEmpty("the sustain window has not elapsed yet");

        (state, IReadOnlyList<SelfMonitoringTransition> t2) =
            SelfMonitoringEvaluator.Evaluate(breach with { ObservedUtc = T0.Add(Thresholds.QueueDepthSustainedFor) }, Thresholds, state);

        t2.Should().ContainSingle(t => t.Metric == SelfMonitoringMetric.QueueDepthSustained && t.Kind == SelfMonitoringTransitionKind.Started);
    }

    [Fact]
    public void Evaluate_AClearedBreach_FiresClearedExactlyOnce()
    {
        SelfMonitoringSnapshot breach = Healthy(T0) with { DiskFreeBytes = 1 };
        (SelfMonitoringState state, _) = SelfMonitoringEvaluator.Evaluate(breach, Thresholds, SelfMonitoringState.Empty);

        (state, IReadOnlyList<SelfMonitoringTransition> cleared) =
            SelfMonitoringEvaluator.Evaluate(Healthy(T0.AddMinutes(1)), Thresholds, state);
        cleared.Should().ContainSingle(t => t.Metric == SelfMonitoringMetric.DiskFree && t.Kind == SelfMonitoringTransitionKind.Cleared);

        (_, IReadOnlyList<SelfMonitoringTransition> stillHealthy) =
            SelfMonitoringEvaluator.Evaluate(Healthy(T0.AddMinutes(2)), Thresholds, state);
        stillHealthy.Should().BeEmpty("already cleared — must not repeat");
    }

    [Fact]
    public void Evaluate_AnAlreadyNotifiedBreachThatPersists_DoesNotRefireEveryTick()
    {
        SelfMonitoringSnapshot breach = Healthy(T0) with { ListenersDown = ["tcp:0.0.0.0:514"] };
        (SelfMonitoringState state, IReadOnlyList<SelfMonitoringTransition> first) =
            SelfMonitoringEvaluator.Evaluate(breach, Thresholds, SelfMonitoringState.Empty);
        first.Should().HaveCount(1);

        (_, IReadOnlyList<SelfMonitoringTransition> second) =
            SelfMonitoringEvaluator.Evaluate(breach with { ObservedUtc = T0.AddMinutes(1) }, Thresholds, state);

        second.Should().BeEmpty();
    }
}
