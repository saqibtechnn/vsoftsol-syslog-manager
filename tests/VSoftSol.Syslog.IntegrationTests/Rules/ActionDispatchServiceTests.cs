using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>PHASE_07 — the dispatcher drains the outbox off-thread: claim → execute → audit → state.</summary>
public sealed class ActionDispatchServiceTests
{
    private static async Task<(long EventId, long RuleId)> SeedAsync(SqliteTestDatabase db, IReadOnlyList<PendingRuleAction> actions)
    {
        var store = new SqliteRuleStore(db.Factory);
        long ruleId = await store.CreateAsync(
            new RuleDefinition { Name = "r", Actions = [new RaiseNotificationAction { Title = "x" }] }, "admin", CancellationToken.None);

        var rekeyed = actions.Select(a => a with { RuleId = ruleId }).ToArray();
        SyslogEvent e = SampleEvents.Minimal("something happened").WithRuleOutcome([], [], rekeyed);
        long eventId = (await db.Repository.AppendBatchAsync([e], CancellationToken.None))[0];
        return (eventId, ruleId);
    }

    private static ActionDispatchService Build(
        SqliteTestDatabase db, ActionExecutorOptions? actionOptions, NotificationSink? notify, out RuleHitTracker hits,
        TimeProvider? time = null)
    {
        hits = new RuleHitTracker();
        time ??= TimeProvider.System;
        return new ActionDispatchService(
            new SqliteActionOutbox(db.Factory, time),
            db.Repository,
            new SqliteAuditLog(db.Factory, time),
            new SqliteRuleStore(db.Factory, time),
            hits,
            new ActionExecutorRegistry(),
            (_, _) => new ValueTask<string?>((string?)null),
            notify ?? ((_, _, _, _, _) => ValueTask.CompletedTask),
            Options.Create(actionOptions ?? new ActionExecutorOptions()),
            Options.Create(new ActionDispatchOptions { PollInterval = TimeSpan.FromMilliseconds(50), MaxAttempts = 2, BackoffBase = TimeSpan.FromSeconds(1) }),
            time,
            NullLogger<ActionDispatchService>.Instance);
    }

    [Fact]
    public async Task Dispatcher_ExecutesAPendingWebhook_MarksItDone_AndAudits()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        string? received = null;
        await using var sink = new HttpSink(async ctx =>
        {
            using var r = new StreamReader(ctx.Request.InputStream);
            received = await r.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
        });

        var webhook = new HttpWebhookAction { Url = sink.Url, BodyTemplate = "{{\"m\":\"{message}\"}}", AllowPrivateNetwork = true };
        var pending = new[] { new PendingRuleAction(0, "r", 0, "webhook", RuleJson.SerializeAction(webhook), false) };
        await SeedAsync(db, pending);

        var options = new ActionExecutorOptions { WebhookPrivateNetworkAllowList = ["127.0.0.0/8"] };
        ActionDispatchService svc = Build(db, options, null, out _);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await WaitForStateAsync(db, "done", TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        received.Should().Be("{\"m\":\"something happened\"}");
        long audits = await ScalarAsync(db, $"SELECT COUNT(*) FROM audit_log WHERE action = '{AuditActions.ActionFired}';");
        audits.Should().Be(1);
    }

    [Fact]
    public async Task Dispatcher_RetriesTransientThenDeadLetters_AndRaisesANotification()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        // Webhook to a black-hole address → transient failure every time.
        var webhook = new HttpWebhookAction { Url = "http://198.51.100.250/hook", TimeoutSeconds = 1 };
        var pending = new[] { new PendingRuleAction(0, "r", 0, "webhook", RuleJson.SerializeAction(webhook), false) };
        await SeedAsync(db, pending);

        // Clock a moment ahead of the just-written row so the first claim is due.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));

        int notifications = 0;
        NotificationSink notify = (_, _, _, _, _) => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; };
        ActionDispatchService svc = Build(db, null, notify, out _, clock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.StartAsync(cts.Token);

        // attempt 1 fails → 'failed' with a back-off
        await WaitForStateAsync(db, "failed", TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));       // clear the back-off
        // attempt 2 fails → MaxAttempts (2) reached → 'dead'
        await WaitForStateAsync(db, "dead", TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        notifications.Should().BeGreaterThanOrEqualTo(1, "a dead-lettered action raises an operator notification");
        long deadAudits = await ScalarAsync(db, $"SELECT COUNT(*) FROM audit_log WHERE action = '{AuditActions.ActionDeadLettered}';");
        deadAudits.Should().Be(1);
    }

    [Fact]
    public async Task Dispatcher_FlushesRuleHitCounters()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteRuleStore(db.Factory);
        long ruleId = await store.CreateAsync(
            new RuleDefinition { Name = "r", Actions = [new RaiseNotificationAction { Title = "x" }] }, "admin", CancellationToken.None);

        ActionDispatchService svc = Build(db, null, null, out RuleHitTracker hits);
        hits.Record(ruleId, DateTimeOffset.UtcNow);
        hits.Record(ruleId, DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await svc.StopAsync(CancellationToken.None); // stop also flushes

        RuleRow? row = await store.GetAsync(ruleId, CancellationToken.None);
        row!.HitCount.Should().Be(2);
    }

    private static async Task WaitForStateAsync(SqliteTestDatabase db, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ScalarAsync(db, $"SELECT COUNT(*) FROM rule_action_queue WHERE state = '{state}';") > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"no outbox row reached state '{state}' within {timeout}");
    }

    private static async Task<long> ScalarAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
