using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 "Storm containment" + the security "notification flooding as an attack" case:
/// an attacker who can generate log events must not be able to weaponise alerting. A flood
/// fires the alert <b>once</b>; a burst of many distinct firings is collapsed to a single
/// summary by the global budget.
/// </summary>
public sealed class AlertStormContainmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFloodOfMatchingEvents_FiresTheAlertOnce_AndNotifiesOnce()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        var alert = new AlertDefinition
        {
            Name = "flood",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 600,
            IntervalSeconds = 60,
            Threshold = 100,
            GroupByField = "hostname",
            Actions = [new RaiseNotificationAction { Title = "flood on {hostname}" }],
            AutoResolve = false,
            ReNotifySeconds = 0,
        };
        await h.Store.CreateAsync(alert, "op", CancellationToken.None);

        // 40,000 matching events in the window, all from one host (a scaled-down but faithful
        // storm — 100k takes minutes to insert on the dev VM; the assertion is identical).
        const int flood = 40_000;
        for (int batch = 0; batch < flood / 5_000; batch++)
        {
            await h.Db.Repository.AppendBatchAsync(
                Enumerable.Range(0, 5_000).Select(i => Event(T0.AddSeconds(-300).AddMilliseconds(batch * 5_000 + i))).ToList(),
                CancellationToken.None);
        }

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        await h.DispatchAsync();

        (await h.OpenInstancesAsync()).Should().ContainSingle();
        (await h.AuditCountAsync(AuditActions.AlertFired)).Should().Be(1);
        h.Notifications.Where(n => n.Title.StartsWith("flood on")).Should().ContainSingle();
    }

    [Fact]
    public async Task ManyDistinctFiringsAtOnce_AreCollapsedToASummary_ByTheGlobalBudget()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(
            T0, configureRuntime: o => o.GlobalActionsPerMinute = 3);

        var alert = new AlertDefinition
        {
            Name = "per-host burst",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 3600,
            IntervalSeconds = 60,
            Threshold = 2,
            GroupByField = "hostname",
            Actions = [new RaiseNotificationAction { Title = "burst on {hostname}" }],
            AutoResolve = false,
            ReNotifySeconds = 0,
        };
        await h.Store.CreateAsync(alert, "op", CancellationToken.None);

        // 20 hosts each breach in the same evaluation tick.
        var events = new List<SyslogEvent>();
        for (int host = 0; host < 20; host++)
        {
            events.AddRange(Enumerable.Range(0, 5).Select(i => Event(T0.AddSeconds(-100 + i), $"host-{host}")));
        }

        await h.Db.Repository.AppendBatchAsync(events, CancellationToken.None);
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        await h.DispatchAsync();

        (await h.OpenInstancesAsync()).Should().HaveCount(20, "every breach still opens an instance — the UI shows them all");

        int perHostNotifications = h.Notifications.Count(n => n.Title.StartsWith("burst on"));
        perHostNotifications.Should().Be(3, "only the budget's worth of notifications went out");

        // the collapse-to-summary path triggered
        h.Notifications.Should().Contain(n => n.Title == "Alert-storm protection engaged");
    }

    private static SyslogEvent Event(DateTimeOffset received, string host = "core-sw-1") => new()
    {
        ReceivedUtc = received,
        SourceIp = "203.0.113.9",
        Hostname = host,
        Facility = Facility.Local0,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = "x",
        RawMessage = Encoding.UTF8.GetBytes("x"),
        ParseStatus = ParseStatus.Rfc3164,
    };
}
