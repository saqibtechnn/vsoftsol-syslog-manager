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
/// The scheduler end-to-end on a virtual clock (PHASE_08 "Tests to write first" + "Virtual
/// clock time-travel suite"). No test takes longer than the code under test.
/// </summary>
public sealed class AlertEvaluationServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static SyslogEvent Event(DateTimeOffset received, string host = "core-sw-1", string message = "authentication failure for root") => new()
    {
        ReceivedUtc = received,
        SourceIp = "203.0.113.9",
        Hostname = host,
        AppName = "sshd",
        Severity = Severity.Warning,
        Facility = Facility.Local0,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc3164,
    };

    private static AlertDefinition ThresholdAlert(int threshold = 5, int windowSeconds = 300, int intervalSeconds = 60, string? groupBy = null) => new()
    {
        Name = "auth failure burst",
        Type = AlertEvaluationType.Threshold,
        Severity = NotificationLevel.Warning,
        WindowSeconds = windowSeconds,
        IntervalSeconds = intervalSeconds,
        Threshold = threshold,
        GroupByField = groupBy,
        Filter = new ConditionGroup
        {
            Join = ConditionJoin.Or,
            Children = { new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "authentication failure" } },
        },
        Actions = [new RaiseNotificationAction { Title = "Auth burst on {hostname}", Body = "{field.alert_value} failures" }],
        AutoResolve = true,
        ReNotifySeconds = 0,
    };

    [Fact]
    public async Task Threshold_ExactlyAtN_DoesNotFire_NPlusOne_Fires()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        await h.Store.CreateAsync(ThresholdAlert(threshold: 5), "op", CancellationToken.None);

        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 5).Select(i => Event(T0.AddSeconds(-60 + i))).ToList(), CancellationToken.None);
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        (await h.OpenInstancesAsync()).Should().BeEmpty("5 == threshold does not fire");

        await h.Db.Repository.AppendBatchAsync([Event(T0.AddSeconds(-30))], CancellationToken.None);
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        (await h.OpenInstancesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Dedup_ConditionTrueForTenEvaluations_ProducesOneOpenInstance()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        await h.Store.CreateAsync(ThresholdAlert(threshold: 3, windowSeconds: 3600), "op", CancellationToken.None);
        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 10).Select(i => Event(T0.AddSeconds(-100 + i))).ToList(), CancellationToken.None);

        for (int i = 0; i < 10; i++)
        {
            await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        }

        (await h.OpenInstancesAsync()).Should().ContainSingle("a condition that stays true re-notifies the same instance, never opens a new one");
        (await h.AuditCountAsync(AuditActions.AlertFired)).Should().Be(1);
    }

    [Fact]
    public async Task Grouped_FiresOncePerBreachingGroup_NotForQuietGroups()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        await h.Store.CreateAsync(ThresholdAlert(threshold: 2, windowSeconds: 3600, groupBy: "hostname"), "op", CancellationToken.None);

        var events = new List<SyslogEvent>();
        events.AddRange(Enumerable.Range(0, 4).Select(i => Event(T0.AddSeconds(-100 + i), host: "core-sw-1")));
        events.AddRange(Enumerable.Range(0, 1).Select(i => Event(T0.AddSeconds(-100 + i), host: "edge-fw")));
        await h.Db.Repository.AppendBatchAsync(events, CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));

        IReadOnlyList<AlertInstance> open = await h.OpenInstancesAsync();
        open.Should().ContainSingle();
        open[0].GroupValue.Should().Be("core-sw-1");
    }

    [Fact]
    public async Task AutoResolve_WhenTheConditionClears()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        await h.Store.CreateAsync(ThresholdAlert(threshold: 2, windowSeconds: 120), "op", CancellationToken.None);
        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 4).Select(i => Event(T0.AddSeconds(-30 + i))).ToList(), CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        (await h.OpenInstancesAsync()).Should().ContainSingle();

        // move the clock past the window so the events fall out of it
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(200));

        (await h.OpenInstancesAsync()).Should().BeEmpty("the condition cleared, so the instance auto-resolved");
        (await h.AuditCountAsync(AuditActions.AlertAutoResolved)).Should().Be(1);
    }

    [Fact]
    public async Task TimeTravel_ThirtyDays_ProducesTheExactFiringCount()
    {
        // One alert, interval 1 h, window 1 h, threshold 0 (i.e. fire on any matching event
        // in the last hour). Feed exactly one matching event per hour for 30 days, resolving
        // in between; assert 30 * 24 firings.
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        var alert = new AlertDefinition
        {
            Name = "hourly heartbeat check",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 3600,
            IntervalSeconds = 3600,
            Threshold = 5,
            GroupByField = "hostname",
            Filter = new ConditionGroup
            {
                Join = ConditionJoin.Or,
                Children = { new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "burst" } },
            },
            Actions = [],
            AutoResolve = true,
            ReNotifySeconds = 0,
        };
        await h.Store.CreateAsync(alert, "op", CancellationToken.None);

        for (int hour = 0; hour < 30 * 24; hour++)
        {
            DateTimeOffset at = T0.AddHours(hour);
            // 6 matching events in this hour, from a host unique to the hour → a fresh
            // breaching group each hour (last hour's group falls quiet and auto-resolves).
            await h.Db.Repository.AppendBatchAsync(
                Enumerable.Range(0, 6).Select(i => Event(at.AddMinutes(i), host: $"host-{hour}", message: $"burst {hour}")).ToList(),
                CancellationToken.None);

            h.Clock.Advance(TimeSpan.FromHours(1));
            await h.TickAsync();
        }

        ((int)await h.AuditCountAsync(AuditActions.AlertFired)).Should().Be(30 * 24);
        (await h.OpenInstancesAsync()).Should().ContainSingle("only the final hour's group is still breaching");
    }

    [Fact]
    public async Task InMemoryScan_HittingItsCap_IsDetected_AndRaisesTheScanCapNotification()
    {
        // v1.1 — found while building the P8-1 alert-preview full replay: InMemoryAsync
        // requested exactly MaxWindowScan rows from StreamWindowAsync, whose own SQL already
        // applies that as a LIMIT — so "scanned > MaxWindowScan" could never be true and
        // AlertWindowData.Truncated could never become true for a filtered alert, no matter
        // how many matching events existed. Ten matching events against a cap of five must
        // be detected as truncated and raise the operator-facing "hit its scan cap" diagnostic
        // (EvaluateOneAsync), not silently evaluate as if there were only five.
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0, configureEval: o => o.MaxWindowScan = 5);
        await h.Store.CreateAsync(ThresholdAlert(threshold: 1, windowSeconds: 300), "op", CancellationToken.None);

        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 10).Select(i => Event(T0.AddSeconds(-60 + i))).ToList(), CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));

        h.Notifications.Should().Contain(n => n.Title.Contains("hit its scan cap", StringComparison.Ordinal));
    }
}
