using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 "Restart test" + "Crash-during-evaluation": kill mid-schedule, restart, assert
/// no duplicate fire, no missed window, and no stuck-open alert. Evaluation state
/// (<c>alert_eval_runs</c>) and the instance dedup key survive the restart.
/// </summary>
public sealed class AlertRestartTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static AlertDefinition Alert() => new()
    {
        Name = "auth burst",
        Type = AlertEvaluationType.Threshold,
        WindowSeconds = 3600,
        IntervalSeconds = 300,
        Threshold = 3,
        ReNotifySeconds = 0,
        AutoResolve = false,
        Filter = new ConditionGroup
        {
            Join = ConditionJoin.Or,
            Children = { new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "fail" } },
        },
        Actions = [new RaiseNotificationAction { Title = "burst" }],
    };

    private static SyslogEvent Event(DateTimeOffset received) => new()
    {
        ReceivedUtc = received,
        SourceIp = "203.0.113.9",
        Hostname = "core-sw-1",
        Facility = Facility.Local0,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = "login fail",
        RawMessage = Encoding.UTF8.GetBytes("login fail"),
        ParseStatus = ParseStatus.Rfc3164,
    };

    [Fact]
    public async Task Restart_AfterAFiring_DoesNotDoubleFire()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        await h.Store.CreateAsync(Alert(), "op", CancellationToken.None);
        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 6).Select(i => Event(T0.AddSeconds(-200 + i))).ToList(), CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        (await h.OpenInstancesAsync()).Should().ContainSingle();
        (await h.AuditCountAsync(AuditActions.AlertFired)).Should().Be(1);

        AlertEvaluationHarness h2 = AlertEvaluationHarness.Reopen(h, T0.AddSeconds(30));
        await h2.TickAsync(); // condition still true, but the instance is already open

        (await h2.OpenInstancesAsync()).Should().ContainSingle();
        (await h2.AuditCountAsync(AuditActions.AlertFired)).Should().Be(1, "the restart did not re-fire");
    }

    [Fact]
    public async Task Restart_MidSchedule_CatchesUpTheMissedWindowWithoutSkipping()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(
            T0, configureEval: o => o.MissedRunGraceMultiplier = 2);
        await h.Store.CreateAsync(Alert(), "op", CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1)); // first evaluation, nothing to fire

        // process "down" for 40 minutes (8 missed 5-minute intervals); events arrive
        AlertEvaluationHarness h2 = AlertEvaluationHarness.Reopen(h, T0.AddMinutes(40), o => o.MissedRunGraceMultiplier = 2);
        await h2.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 6).Select(i => Event(T0.AddMinutes(38).AddSeconds(i))).ToList(), CancellationToken.None);

        await h2.TickAsync();

        (await h2.OpenInstancesAsync()).Should().ContainSingle("the catch-up evaluation still fires for the current window");
        (await h2.AuditCountAsync(AuditActions.AlertEvaluationMissed)).Should().BeGreaterThan(0, "the missed run was logged, not silently skipped");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CrashAtVariousPointsInReconcile_ConvergesToOneOpenInstance(int killAfterOperations)
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        long alertId = await h.Store.CreateAsync(Alert(), "op", CancellationToken.None);
        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 6).Select(i => Event(T0.AddSeconds(-200 + i))).ToList(), CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromSeconds(1));

        // Simulate a partial reconcile: open the instance (as the service would), then
        // "crash" after N of the follow-up storage writes.
        (long instanceId, bool created) = await h.Instances.OpenAsync(
            alertId, NotificationLevel.Warning, groupValue: null, 6, 3, [], h.Clock.GetUtcNow(), CancellationToken.None);
        created.Should().BeTrue();

        if (killAfterOperations >= 1)
        {
            await h.Store.RecordFiredAsync(alertId, h.Clock.GetUtcNow(), CancellationToken.None);
        }

        if (killAfterOperations >= 2)
        {
            await h.Instances.MarkNotifiedAsync(instanceId, h.Clock.GetUtcNow(), CancellationToken.None);
        }

        if (killAfterOperations >= 3)
        {
            await h.Store.RecordEvalRunAsync(alertId, h.Clock.GetUtcNow(), h.Clock.GetUtcNow(), "ok", false, CancellationToken.None);
        }

        AlertEvaluationHarness h2 = AlertEvaluationHarness.Reopen(h, T0.AddSeconds(2));
        await h2.TickAsync();

        (await h2.OpenInstancesAsync()).Should().ContainSingle("every crash point converges to exactly one open instance");
    }
}
