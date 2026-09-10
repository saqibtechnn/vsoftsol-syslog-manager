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
/// PHASE_08 "Re-notify test": a firing alert does not re-notify every evaluation; it
/// re-notifies only on the configured interval.
/// </summary>
public sealed class AlertReNotifyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static SyslogEvent Event(DateTimeOffset received) => new()
    {
        ReceivedUtc = received,
        SourceIp = "203.0.113.9",
        Hostname = "core-sw-1",
        Facility = Facility.Local0,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = "authentication failure",
        RawMessage = Encoding.UTF8.GetBytes("authentication failure"),
        ParseStatus = ParseStatus.Rfc3164,
    };

    [Fact]
    public async Task FiringAlert_ReNotifiesOnlyOnTheConfiguredInterval()
    {
        AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        var alert = new AlertDefinition
        {
            Name = "auth burst",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 3600,
            IntervalSeconds = 60,
            Threshold = 2,
            ReNotifySeconds = 600, // re-notify every 10 minutes while open
            AutoResolve = false,
            Filter = new ConditionGroup
            {
                Join = ConditionJoin.Or,
                Children = { new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "authentication failure" } },
            },
            Actions = [new RaiseNotificationAction { Title = "Auth burst" }],
        };
        await h.Store.CreateAsync(alert, "op", CancellationToken.None);
        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 10).Select(i => Event(T0.AddSeconds(-300 + i))).ToList(), CancellationToken.None);

        // first tick opens the instance and queues the opening notification (seq 0)
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        await h.DispatchAsync();
        int notifications = h.Notifications.Count;
        notifications.Should().Be(1);

        // five more ticks over the next 5 minutes — still inside the re-notify interval
        for (int i = 0; i < 5; i++)
        {
            await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        }

        await h.DispatchAsync();
        h.Notifications.Count.Should().Be(notifications, "no re-notify before the interval elapses");

        // cross the 10-minute mark
        for (int i = 0; i < 6; i++)
        {
            await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(60));
        }

        await h.DispatchAsync();
        h.Notifications.Count.Should().Be(notifications + 1, "one re-notify after the interval");
        (await h.AuditCountAsync(AuditActions.AlertRenotified)).Should().Be(1);
    }
}
