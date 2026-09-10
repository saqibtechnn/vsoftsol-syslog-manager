using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Alerts;

/// <summary>One claimed row from the alert action outbox, ready to execute.</summary>
public sealed record QueuedAlertAction(
    long QueueId,
    long AlertId,
    long InstanceId,
    int ActionIndex,
    int NotifySeq,
    string Kind,
    string PayloadJson,
    int Attempts);

/// <summary>Counts by outbox state.</summary>
public readonly record struct AlertOutboxStats(long Pending, long Running, long Failed, long Dead, long Done);

/// <summary>
/// The crash-safe outbox for alert-triggered actions (ADR 0016) — the same shape as the
/// Phase 7 <see cref="Rules.SqliteActionOutbox"/>, but written by the
/// <c>AlertEvaluationService</c> (not the ingest transaction) and keyed by
/// <c>(instance_id, action_index, notify_seq)</c> so a re-notify round and a restart mid-
/// dispatch are both idempotent. Drained by <c>AlertActionDispatchService</c>.
/// </summary>
public sealed class SqliteAlertActionOutbox(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Enqueues every action for one notification round of an instance. <c>INSERT OR IGNORE</c>
    /// on the UNIQUE key means a retry of the same round is a no-op. Returns the number of
    /// rows actually inserted.
    /// </summary>
    public async Task<int> EnqueueAsync(
        long alertId,
        long instanceId,
        int notifySeq,
        IReadOnlyList<RuleAction> actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return 0;
        }

        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO alert_action_queue
              (alert_id, instance_id, action_index, notify_seq, kind, payload_json, next_attempt_utc, created_utc)
            VALUES ($alert, $instance, $index, $seq, $kind, $payload, $now, $now);
            """;
        SqliteParameter alertP = command.Parameters.Add("$alert", SqliteType.Integer);
        SqliteParameter instanceP = command.Parameters.Add("$instance", SqliteType.Integer);
        SqliteParameter indexP = command.Parameters.Add("$index", SqliteType.Integer);
        SqliteParameter seqP = command.Parameters.Add("$seq", SqliteType.Integer);
        SqliteParameter kindP = command.Parameters.Add("$kind", SqliteType.Text);
        SqliteParameter payloadP = command.Parameters.Add("$payload", SqliteType.Text);
        command.Parameters.AddWithValue("$now", now);

        alertP.Value = alertId;
        instanceP.Value = instanceId;
        seqP.Value = notifySeq;

        int inserted = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            indexP.Value = i;
            kindP.Value = RuleActionInfo.Kind(actions[i]);
            payloadP.Value = AlertJson.SerializeAction(actions[i]);
            inserted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<IReadOnlyList<QueuedAlertAction>> ClaimBatchAsync(int max, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_action_queue
               SET state = 'running', attempts = attempts + 1
             WHERE queue_id IN (
                   SELECT queue_id FROM alert_action_queue
                    WHERE state IN ('pending', 'failed') AND next_attempt_utc <= $now
                    ORDER BY next_attempt_utc
                    LIMIT $max)
            RETURNING queue_id, alert_id, instance_id, action_index, notify_seq, kind, payload_json, attempts;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$max", max);

        var claimed = new List<QueuedAlertAction>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new QueuedAlertAction(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (int)reader.GetInt64(3),
                (int)reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
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

    public async Task<int> RecoverStaleRunningAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_action_queue
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
            "DELETE FROM alert_action_queue WHERE state IN ('done', 'dead') AND completed_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", StorageFormat.Timestamp(olderThanUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlertOutboxStats> StatsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM alert_action_queue GROUP BY state;";
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return new AlertOutboxStats(
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
            UPDATE alert_action_queue
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
