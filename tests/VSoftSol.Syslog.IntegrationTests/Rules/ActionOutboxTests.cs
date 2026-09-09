using FluentAssertions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

public sealed class ActionOutboxTests
{
    private static PendingRuleAction Pending(int index = 0, bool esc = false) =>
        new(RuleId: 1, RuleName: "r", ActionIndex: index, Kind: "notify", PayloadJson: "{\"kind\":\"notify\",\"title\":\"x\"}", WasEscalation: esc);

    private static async Task<long> SeedRuleAsync(SqliteTestDatabase db)
    {
        await using var c = await db.Factory.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO rules (name, priority, created_utc, updated_utc) VALUES ('r', 100, 't', 't'); SELECT last_insert_rowid();";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Repository_WritesOutboxRowsInTheEventTransaction()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await SeedRuleAsync(db);

        SyslogEvent e = SampleEvents.Minimal("disk full")
            .WithRuleOutcome([], [], [Pending(0), Pending(1)]);
        IReadOnlyList<long> ids = await db.Repository.AppendBatchAsync([e], CancellationToken.None);

        var outbox = new SqliteActionOutbox(db.Factory);
        IReadOnlyList<QueuedAction> claimed = await outbox.ClaimBatchAsync(10, CancellationToken.None);

        claimed.Should().HaveCount(2);
        claimed.Should().OnlyContain(a => a.EventId == ids[0] && a.RuleId == 1);
        claimed.Select(a => a.ActionIndex).Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact]
    public async Task Repository_ReAppendingTheSameRuleEventAction_DoesNotDoubleEnqueue()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await SeedRuleAsync(db);

        // Simulate a crash-and-replay: the same event id would only recur if the events row
        // is re-inserted, which cannot happen (AUTOINCREMENT). Assert the UNIQUE key holds
        // even under a direct duplicate enqueue.
        SyslogEvent e = SampleEvents.Minimal().WithRuleOutcome([], [], [Pending(0)]);
        long id = (await db.Repository.AppendBatchAsync([e], CancellationToken.None))[0];

        await using (var c = await db.Factory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO rule_action_queue (rule_id, action_index, event_id, kind, payload_json, next_attempt_utc, created_utc)
                VALUES (1, 0, $e, 'notify', '{}', 't', 't');
                """;
            cmd.Parameters.AddWithValue("$e", id);
            await cmd.ExecuteNonQueryAsync();
        }

        var outbox = new SqliteActionOutbox(db.Factory);
        ActionOutboxStats stats = await outbox.StatsAsync(CancellationToken.None);
        (stats.Pending + stats.Running).Should().Be(1);
    }

    [Fact]
    public async Task Claim_Fail_ThenReclaimAfterBackoff()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await SeedRuleAsync(db);
        SyslogEvent e = SampleEvents.Minimal().WithRuleOutcome([], [], [Pending(0)]);
        await db.Repository.AppendBatchAsync([e], CancellationToken.None);

        var outbox = new SqliteActionOutbox(db.Factory, clock);
        QueuedAction first = (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Single();
        first.Attempts.Should().Be(1);

        await outbox.FailAsync(first.QueueId, clock.GetUtcNow().AddMinutes(5), "smtp refused", CancellationToken.None);
        (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Should().BeEmpty("still in back-off");

        clock.Advance(TimeSpan.FromMinutes(6));
        QueuedAction retry = (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Single();
        retry.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task DeadLetter_AndPurge()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await SeedRuleAsync(db);
        SyslogEvent e = SampleEvents.Minimal().WithRuleOutcome([], [], [Pending(0)]);
        await db.Repository.AppendBatchAsync([e], CancellationToken.None);

        var outbox = new SqliteActionOutbox(db.Factory, clock);
        QueuedAction a = (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Single();
        await outbox.DeadLetterAsync(a.QueueId, "gave up after 5 attempts", CancellationToken.None);

        (await outbox.StatsAsync(CancellationToken.None)).Dead.Should().Be(1);

        clock.Advance(TimeSpan.FromDays(31));
        int purged = await outbox.PurgeCompletedAsync(clock.GetUtcNow().AddDays(-30), CancellationToken.None);
        purged.Should().Be(1);
    }

    [Fact]
    public async Task RecoverStaleRunning_MovesAbandonedRowsBackToFailed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await SeedRuleAsync(db);
        SyslogEvent e = SampleEvents.Minimal().WithRuleOutcome([], [], [Pending(0)]);
        await db.Repository.AppendBatchAsync([e], CancellationToken.None);

        var outbox = new SqliteActionOutbox(db.Factory, clock);
        await outbox.ClaimBatchAsync(10, CancellationToken.None); // now 'running'

        clock.Advance(TimeSpan.FromMinutes(30));
        int recovered = await outbox.RecoverStaleRunningAsync(clock.GetUtcNow().AddMinutes(-10), CancellationToken.None);
        recovered.Should().Be(1);
        (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Should().ContainSingle();
    }
}
