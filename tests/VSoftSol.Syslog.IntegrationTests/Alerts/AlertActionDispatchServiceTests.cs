using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// The alert action dispatcher: claim → execute (with a synthetic event) → audit; a
/// transient failure backs off and retries; a permanent failure dead-letters and raises an
/// operator notification. Mirrors the Phase 7 <c>ActionDispatchServiceTests</c>.
/// </summary>
public sealed class AlertActionDispatchServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(long AlertId, long InstanceId)> SeedFiringAsync(AlertEvaluationHarness h, NotificationLevel level = NotificationLevel.Warning)
    {
        long alertId = await h.Store.CreateAsync(new Core.Alerts.AlertDefinition
        {
            Name = "a",
            Type = Core.Alerts.AlertEvaluationType.Threshold,
            Severity = level,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 1,
            RemediationNotes = "restart the service",
        }, "op", CancellationToken.None);

        (long instanceId, _) = await h.Instances.OpenAsync(
            alertId, level, "core-sw-1", observedValue: 9, threshold: 1, [], T0, CancellationToken.None);
        return (alertId, instanceId);
    }

    [Fact]
    public async Task Notification_RendersFromTheSyntheticEvent_AndAuditsFired()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        (long alertId, long instanceId) = await SeedFiringAsync(h, NotificationLevel.Critical);

        await h.Outbox.EnqueueAsync(alertId, instanceId, 0,
            [new RaiseNotificationAction { Level = NotificationLevel.Warning, Title = "Alert on {hostname}", Body = "value {field.alert_value}, threshold {field.alert_threshold}" }],
            CancellationToken.None);

        await h.DispatchAsync();

        RecordedNotification n = h.Notifications.Single();
        n.Title.Should().Be("Alert on core-sw-1");
        n.Body.Should().Contain("value 9").And.Contain("threshold 1");
        n.RuleId.Should().Be(alertId, "the notification is attributed to the alert");
        (await h.AuditCountAsync(AuditActions.ActionFired)).Should().Be(1);
    }

    [Fact]
    public async Task TransientFailure_BacksOff_ThenRetries_ThenDeadLettersWithNotification()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(
            T0, configureEval: o =>
            {
                o.DispatchMaxAttempts = 2;
                o.DispatchBackoffBase = TimeSpan.FromSeconds(30);
            });
        (long alertId, long instanceId) = await SeedFiringAsync(h);

        // a webhook to a black-hole address → SSRF-guard refuses (permanent) OR times out
        // (transient). Use an unroutable public IP + tiny timeout → transient.
        await h.Outbox.EnqueueAsync(alertId, instanceId, 0,
            [new HttpWebhookAction { Url = "http://198.51.100.9/hook", TimeoutSeconds = 1, AllowPrivateNetwork = false }],
            CancellationToken.None);

        await h.DispatchAsync();
        (await h.Outbox.StatsAsync(CancellationToken.None)).Failed.Should().Be(1);

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.DispatchAsync();

        AlertOutboxStats stats = await h.Outbox.StatsAsync(CancellationToken.None);
        stats.Dead.Should().Be(1, "gave up after the max attempts");
        h.Notifications.Should().Contain(n => n.Title == "Alert action failed permanently");
        (await h.AuditCountAsync(AuditActions.ActionDeadLettered)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UnparseablePayload_IsDeadLettered()
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);
        (long alertId, long instanceId) = await SeedFiringAsync(h);

        await using (var c = await h.Db.Factory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO alert_action_queue (alert_id, instance_id, action_index, notify_seq, kind, payload_json, next_attempt_utc, created_utc)
                VALUES ($a, $i, 0, 0, 'notify', 'not json at all', $now, $now);
                """;
            cmd.Parameters.AddWithValue("$a", alertId);
            cmd.Parameters.AddWithValue("$i", instanceId);
            cmd.Parameters.AddWithValue("$now", h.Clock.GetUtcNow().UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await h.DispatchAsync();
        (await h.Outbox.StatsAsync(CancellationToken.None)).Dead.Should().Be(1);
    }
}
