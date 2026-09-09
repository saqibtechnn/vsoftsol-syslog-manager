using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 — "the single most important test in the phase": a rule action that blocks for
/// a long time must not stall ingestion. Rule evaluation only <b>enqueues</b> to the outbox
/// (a fast in-transaction write); execution happens on a separate service, so throughput is
/// unaffected by a slow or hung action.
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class RuleIngestIsolationTests(ITestOutputHelper output)
{
    private static async Task<(IngestionHarness Harness, SqliteRuleStore Store)> HarnessWithRulesAsync()
    {
        SqliteRuleStore? capturedStore = null;
        IngestionHarness harness = await IngestionHarness.CreateAsync(enricherFactory: testDb =>
        {
            (var enricher, _, SqliteRuleStore store) = RuleEnricherFactory.Build(testDb);
            capturedStore = store;
            return enricher;
        });
        return (harness, capturedStore!);
    }

    [Fact]
    public async Task SlowAction_DoesNotStallIngest_ActionsLandInTheOutbox()
    {
        (IngestionHarness h, SqliteRuleStore store) = await HarnessWithRulesAsync();
        await using IngestionHarness _ = h;

        await store.CreateAsync(new RuleDefinition
        {
            Name = "blocks forever",
            Priority = 10,
            Filter = null, // matches every message
            Actions = [new HttpWebhookAction { Url = "http://198.51.100.9/hook", TimeoutSeconds = 120 }],
        }, "admin", CancellationToken.None);

        h.StartPipeline();

        const int messages = 2_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < messages; i++)
        {
            await h.Intake.AcceptAsync(new VSoftSol.Syslog.Ingestion.RawFrame(
                DateTimeOffset.UtcNow, "203.0.113.5", "udp:test", Protocol.Udp,
                System.Text.Encoding.UTF8.GetBytes($"<38>Mar  9 10:00:{i % 60:D2} fw01 sshd[{i}]: message {i}"),
                truncated: false), default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(60));
        sw.Stop();

        (await h.CommittedCountAsync()).Should().Be(messages, "every message is stored regardless of the action");
        double perSecond = messages / sw.Elapsed.TotalSeconds;
        output.WriteLine($"{messages} messages committed in {sw.Elapsed.TotalSeconds:F1}s => {perSecond:N0} msg/sec");
        perSecond.Should().BeGreaterThan(500, "the blocking action never touched the ingest thread");

        long pending = await ScalarAsync(h.Db, "SELECT COUNT(*) FROM rule_action_queue WHERE state = 'pending';");
        pending.Should().Be(messages);
    }

    [Fact]
    public async Task Enricher_AppliesInlineActions_AndEnqueuesSideEffectingOnes()
    {
        (IngestionHarness h, SqliteRuleStore store) = await HarnessWithRulesAsync();
        await using IngestionHarness _ = h;

        await store.CreateAsync(new RuleDefinition
        {
            Name = "tag + notify auth failures",
            Priority = 20,
            Filter = new Core.Conditions.ConditionGroup
            {
                Join = Core.Conditions.ConditionJoin.Or,
                Children = { new Core.Conditions.ConditionComparison { Field = "message", Operator = Core.Conditions.ConditionOperator.Contains, Value = "authentication failure" } },
            },
            Actions =
            [
                new AddTagAction { Tag = "auth-fail" },
                new RaiseNotificationAction { Title = "Auth failure on {hostname}", Body = "{message}" },
            ],
        }, "admin", CancellationToken.None);

        h.StartPipeline();
        await h.Intake.AcceptAsync(new VSoftSol.Syslog.Ingestion.RawFrame(
            DateTimeOffset.UtcNow, "203.0.113.5", "udp:test", Protocol.Udp,
            System.Text.Encoding.UTF8.GetBytes("<38>Mar  9 10:00:00 fw01 sshd[1]: authentication failure for root"),
            truncated: false), default);
        await h.DrainAsync();

        long tagged = await ScalarAsync(h.Db,
            "SELECT COUNT(*) FROM event_fields WHERE name = 'tag' AND value = 'auth-fail';");
        tagged.Should().Be(1, "the inline AddTag action ran before commit");

        long queued = await ScalarAsync(h.Db, "SELECT COUNT(*) FROM rule_action_queue WHERE kind = 'notify';");
        queued.Should().Be(1, "the side-effecting RaiseNotification action was enqueued");
    }

    private static async Task<long> ScalarAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
