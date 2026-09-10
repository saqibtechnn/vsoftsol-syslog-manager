using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// The alert action outbox — the same crash-safe contract as the Phase 7
/// <c>SqliteActionOutbox</c> (claim → run → complete / fail-with-backoff / dead-letter,
/// stale recovery, purge), keyed by <c>(instance_id, action_index, notify_seq)</c>.
/// </summary>
public sealed class AlertActionOutboxTests
{
    private static async Task<(long AlertId, long InstanceId)> SeedAsync(SqliteTestDatabase db)
    {
        await using var c = await db.Factory.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alert_definitions (name, eval_type, window_seconds, interval_seconds, created_utc, updated_utc)
            VALUES ('a', 'threshold', 60, 60, 't', 't');
            INSERT INTO alert_instances (alert_id, group_value, state, severity, observed_value, threshold, opened_utc)
            VALUES (1, 'g', 'firing', 'warning', 10, 5, 't');
            SELECT 1, 1;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static RaiseNotificationAction Notify() => new() { Title = "x" };

    [Fact]
    public async Task Enqueue_IsIdempotentPerRound_AndClaimReturnsThem()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        (long alertId, long instanceId) = await SeedAsync(db);
        var outbox = new SqliteAlertActionOutbox(db.Factory);

        await outbox.EnqueueAsync(alertId, instanceId, notifySeq: 0, [Notify(), Notify()], CancellationToken.None);
        int again = await outbox.EnqueueAsync(alertId, instanceId, notifySeq: 0, [Notify(), Notify()], CancellationToken.None);
        again.Should().Be(0, "the same round is already queued");

        IReadOnlyList<QueuedAlertAction> claimed = await outbox.ClaimBatchAsync(10, CancellationToken.None);
        claimed.Should().HaveCount(2);
        claimed.Should().OnlyContain(a => a.InstanceId == instanceId && a.NotifySeq == 0);
        claimed.Select(a => a.ActionIndex).Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact]
    public async Task Claim_Fail_BacksOff_ThenReclaims()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        (long alertId, long instanceId) = await SeedAsync(db);
        var outbox = new SqliteAlertActionOutbox(db.Factory, clock);
        await outbox.EnqueueAsync(alertId, instanceId, 0, [Notify()], CancellationToken.None);

        QueuedAlertAction first = (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Single();
        first.Attempts.Should().Be(1);

        await outbox.FailAsync(first.QueueId, clock.GetUtcNow().AddMinutes(5), "smtp refused", CancellationToken.None);
        (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Should().BeEmpty("still in back-off");

        clock.Advance(TimeSpan.FromMinutes(6));
        (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Single().Attempts.Should().Be(2);
    }

    [Fact]
    public async Task DeadLetter_ThenPurge()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        (long alertId, long instanceId) = await SeedAsync(db);
        var outbox = new SqliteAlertActionOutbox(db.Factory, clock);
        await outbox.EnqueueAsync(alertId, instanceId, 0, [Notify()], CancellationToken.None);

        QueuedAlertAction a = (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Single();
        await outbox.DeadLetterAsync(a.QueueId, "gave up", CancellationToken.None);
        (await outbox.StatsAsync(CancellationToken.None)).Dead.Should().Be(1);

        clock.Advance(TimeSpan.FromDays(31));
        (await outbox.PurgeCompletedAsync(clock.GetUtcNow().AddDays(-30), CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task RecoverStaleRunning_MovesAbandonedRowsBackToFailed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        (long alertId, long instanceId) = await SeedAsync(db);
        var outbox = new SqliteAlertActionOutbox(db.Factory, clock);
        await outbox.EnqueueAsync(alertId, instanceId, 0, [Notify()], CancellationToken.None);
        await outbox.ClaimBatchAsync(10, CancellationToken.None); // now 'running'

        clock.Advance(TimeSpan.FromMinutes(30));
        (await outbox.RecoverStaleRunningAsync(clock.GetUtcNow().AddMinutes(-10), CancellationToken.None)).Should().Be(1);
        (await outbox.ClaimBatchAsync(10, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeletingTheAlert_CascadesTheQueue()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        (long alertId, long instanceId) = await SeedAsync(db);
        var outbox = new SqliteAlertActionOutbox(db.Factory);
        await outbox.EnqueueAsync(alertId, instanceId, 0, [Notify()], CancellationToken.None);

        await using (var c = await db.Factory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM alert_definitions WHERE alert_id = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        (await outbox.StatsAsync(CancellationToken.None)).Should().Be(default(AlertOutboxStats));
    }
}
