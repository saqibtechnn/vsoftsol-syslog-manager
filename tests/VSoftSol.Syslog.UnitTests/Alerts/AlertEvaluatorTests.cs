using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Rules.Alerts;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Alerts.AlertTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Alerts;

public sealed class AlertEvaluatorTests
{
    private static readonly AlertCompiler Compiler = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static CompiledAlert Compile(Core.Alerts.AlertDefinition def)
    {
        AlertCompileResult r = Compiler.Compile(def);
        r.Success.Should().BeTrue(string.Join("; ", r.Errors));
        return r.Alert!;
    }

    private static AlertWindowData Counts(params (string? Group, long Count)[] groups) =>
        new() { GroupCounts = [.. groups.Select(g => new GroupCount(g.Group, g.Count, [1L, 2L]))] };

    [Fact]
    public void Threshold_ExactlyAtN_DoesNotFire()
    {
        AlertEvaluation result = AlertEvaluator.Evaluate(Compile(Threshold(threshold: 5)), Counts((null, 5)), Now);

        result.AnyBreach.Should().BeFalse();
    }

    [Fact]
    public void Threshold_NPlusOne_Fires()
    {
        AlertEvaluation result = AlertEvaluator.Evaluate(Compile(Threshold(threshold: 5)), Counts((null, 6)), Now);

        result.Breaches.Should().ContainSingle();
        result.Breaches[0].ObservedValue.Should().Be(6);
        result.Breaches[0].TriggerEventIds.Should().Equal(1L, 2L);
    }

    [Fact]
    public void Threshold_Grouped_FiresOnlyForTheBreachingGroups()
    {
        CompiledAlert alert = Compile(Threshold(threshold: 3, groupBy: "hostname"));

        AlertEvaluation result = AlertEvaluator.Evaluate(
            alert, Counts(("core-sw-1", 9), ("core-sw-2", 2), ("edge-fw", 4)), Now);

        result.Breaches.Select(b => b.GroupValue).Should().BeEquivalentTo(["core-sw-1", "edge-fw"]);
    }

    [Fact]
    public void DistinctCount_AboveThreshold_Fires_Once()
    {
        CompiledAlert alert = Compile(DistinctCount("source_ip", threshold: 10));
        var data = new AlertWindowData { DistinctValueCount = 11, DistinctSampleEventIds = [7L] };

        AlertEvaluation result = AlertEvaluator.Evaluate(alert, data, Now);

        result.Breaches.Should().ContainSingle();
        result.Breaches[0].ObservedValue.Should().Be(11);
    }

    [Fact]
    public void DistinctCount_AtThreshold_DoesNotFire()
    {
        CompiledAlert alert = Compile(DistinctCount("source_ip", threshold: 10));

        AlertEvaluator.Evaluate(alert, new AlertWindowData { DistinctValueCount = 10 }, Now).AnyBreach.Should().BeFalse();
    }

    [Fact]
    public void Absence_NoMatches_Fires()
    {
        CompiledAlert alert = Compile(Absence(All(Cmp("app", Core.Conditions.ConditionOperator.Equals, "backup"))));

        AlertEvaluator.Evaluate(alert, new AlertWindowData { GroupCounts = [] }, Now).AnyBreach.Should().BeTrue();
    }

    [Fact]
    public void Absence_WithMatches_DoesNotFire()
    {
        CompiledAlert alert = Compile(Absence(All(Cmp("app", Core.Conditions.ConditionOperator.Equals, "backup"))));

        AlertEvaluator.Evaluate(alert, Counts((null, 1)), Now).AnyBreach.Should().BeFalse();
    }

    [Fact]
    public void DeviceSilent_DeviceWithinThreshold_DoesNotFire()
    {
        CompiledAlert alert = Compile(DeviceSilent(windowSeconds: 900));
        var data = new AlertWindowData
        {
            DeviceSilences = [new DeviceSilence(1, "core-sw-1", Now.AddMinutes(-3), ThresholdMinutes: 15)],
        };

        AlertEvaluator.Evaluate(alert, data, Now).AnyBreach.Should().BeFalse();
    }

    [Fact]
    public void DeviceSilent_DeviceOverThreshold_Fires()
    {
        CompiledAlert alert = Compile(DeviceSilent());
        var data = new AlertWindowData
        {
            DeviceSilences = [new DeviceSilence(1, "core-sw-1", Now.AddMinutes(-40), ThresholdMinutes: 15)],
        };

        AlertEvaluation result = AlertEvaluator.Evaluate(alert, data, Now);

        result.Breaches.Should().ContainSingle().Which.GroupValue.Should().Be("core-sw-1");
        result.Breaches[0].ObservedValue.Should().BeCloseTo(40, 1);
    }

    [Fact]
    public void DeviceSilent_DeviceNeverSeen_Fires()
    {
        CompiledAlert alert = Compile(DeviceSilent());
        var data = new AlertWindowData
        {
            DeviceSilences = [new DeviceSilence(2, "new-rtr", LastSeenUtc: null, ThresholdMinutes: 15)],
        };

        AlertEvaluator.Evaluate(alert, data, Now).Breaches.Should().ContainSingle().Which.GroupValue.Should().Be("new-rtr");
    }

    [Fact]
    public void DeviceSilent_ExactlyAtThreshold_DoesNotFire()
    {
        // boundary: "silent for N minutes" fires strictly after N
        CompiledAlert alert = Compile(DeviceSilent());
        var data = new AlertWindowData
        {
            DeviceSilences = [new DeviceSilence(1, "d", Now.AddMinutes(-15), ThresholdMinutes: 15)],
        };

        AlertEvaluator.Evaluate(alert, data, Now).AnyBreach.Should().BeFalse();
    }

    [Fact]
    public void Threshold_TruncatedWindow_ThatAlreadyBreaches_StillFires()
    {
        CompiledAlert alert = Compile(Threshold(threshold: 100));
        var data = new AlertWindowData
        {
            GroupCounts = [new GroupCount(null, 500_000, [1L])],
            Truncated = true,
        };

        AlertEvaluator.Evaluate(alert, data, Now).AnyBreach.Should().BeTrue();
    }
}
