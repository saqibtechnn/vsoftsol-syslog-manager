using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Rules;

/// <summary>One claimed row from the action outbox, ready to execute.</summary>
public sealed record QueuedAction(
    long QueueId,
    long RuleId,
    int ActionIndex,
    long EventId,
    string Kind,
    string PayloadJson,
    bool WasEscalation,
    int Attempts);

/// <summary>Counts by outbox state, for the UI and metrics.</summary>
public readonly record struct ActionOutboxStats(long Pending, long Running, long Failed, long Dead, long Done);

/// <summary>
/// The read / claim / complete side of the <c>rule_action_queue</c> outbox (ADR 0015). The
/// <em>write</em> side is <see cref="Repositories.SqliteLogRepository"/>, which inserts rows
/// in the same transaction as the event so a re-evaluated event cannot double-fire (the
/// UNIQUE <c>(rule_id, event_id, action_index)</c> key). The <see cref="ActionDispatchService"/>
/// claims rows here, executes them off the ingest thread, and reports the result back.
/// </summary>
public sealed class SqliteActionOutbox(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Atomically moves up to <paramref name="max"/> due rows to <c>running</c> and returns
    /// them (oldest first). A row's <c>attempts</c> is incremented on claim.
    /// </summary>
    public async Task<IReadOnlyList<QueuedAction>> ClaimBatchAsync(int max, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rule_action_queue
               SET state = 'running', attempts = attempts + 1
             WHERE queue_id IN (
                   SELECT queue_id FROM rule_action_queue
                    WHERE state IN ('pending', 'failed') AND next_attempt_utc <= $now
                    ORDER BY next_attempt_utc
                    LIMIT $max)
            RETURNING queue_id, rule_id, action_index, event_id, kind, payload_json, was_escalation, attempts;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$max", max);

        var claimed = new List<QueuedAction>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new QueuedAction(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (int)reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6) == 1,
                (int)reader.GetInt64(7)));
        }

        return claimed;
    }

    public Task CompleteAsync(long queueId, CancellationToken cancellationToken) =>
        SetStateAsync(queueId, "done", nextAttempt: null, error: null, completed: true, cancellationToken);

    public Task FailAsync(long queueId, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken) =>
        SetStateAsync(queueId, "failed", nextAttemptUtc, Trim(error), completed: false, cancellationToken);

    public Task DeadLetterAsync(long queueId, string error, CancellationToken cancellationToken) =>
        SetStateAsync(queueId, "dead", nextAttempt: null, Trim(error), completed: true, cancellationToken);

    /// <summary>Returns <c>running</c> rows stuck since before <paramref name="olderThanUtc"/> to <c>failed</c>.</summary>
    public async Task<int> RecoverStaleRunningAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rule_action_queue
               SET state = 'failed', last_error = 'recovered from a stale running state', next_attempt_utc = $now
             WHERE state = 'running' AND created_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$cutoff", StorageFormat.Timestamp(olderThanUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM rule_action_queue WHERE state IN ('done', 'dead') AND completed_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", StorageFormat.Timestamp(olderThanUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionOutboxStats> StatsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM rule_action_queue GROUP BY state;";
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return new ActionOutboxStats(
            counts.GetValueOrDefault("pending"),
            counts.GetValueOrDefault("running"),
            counts.GetValueOrDefault("failed"),
            counts.GetValueOrDefault("dead"),
            counts.GetValueOrDefault("done"));
    }

    private async Task SetStateAsync(
        long queueId, string state, DateTimeOffset? nextAttempt, string? error, bool completed, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rule_action_queue
               SET state = $state,
                   next_attempt_utc = COALESCE($next, next_attempt_utc),
                   completed_utc = CASE WHEN $completed = 1 THEN $now ELSE completed_utc END,
                   last_error = COALESCE($err, last_error)
             WHERE queue_id = $id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$next", nextAttempt is { } n ? StorageFormat.Timestamp(n) : DBNull.Value);
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", queueId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Trim(string error) =>
        string.IsNullOrEmpty(error) ? "(no detail)" : error.Length <= 2000 ? error : error[..2000];
}
