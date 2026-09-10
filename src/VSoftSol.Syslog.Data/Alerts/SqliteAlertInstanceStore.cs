using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Alerts;

/// <summary>A filter for the alert-history page (PHASE_08 build item 6).</summary>
public sealed record AlertHistoryQuery
{
    public long? AlertId { get; init; }

    public AlertState? State { get; init; }

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public bool OpenOnly { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}

/// <summary>
/// The alert-instance lifecycle store (PHASE_08 items 5 &amp; 6). The partial unique index
/// <c>ux_alert_instances_open (alert_id, group_value) WHERE state &lt;&gt; 'resolved'</c> is the
/// deduplication guarantee: <see cref="OpenAsync"/> is idempotent, so a condition that stays
/// true across many evaluations produces exactly one open instance. Every transition is
/// recorded with actor, timestamp, and note.
/// </summary>
public sealed class SqliteAlertInstanceStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    /// <summary>The <c>group_value</c> stored for an alert with no grouping field.</summary>
    public const string UngroupedKey = "";

    private const string Columns =
        "instance_id, alert_id, group_value, state, severity, observed_value, threshold, opened_utc, " +
        "acknowledged_utc, acknowledged_by, resolved_utc, resolved_by, auto_resolved, last_notified_utc, note";

    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AlertInstance?> FindOpenAsync(long alertId, string? groupValue, CancellationToken cancellationToken)
    {
        IReadOnlyList<AlertInstance> rows = await QueryAsync(
            "WHERE alert_id = $a AND group_value = $g AND state <> 'resolved'",
            c =>
            {
                c.Parameters.AddWithValue("$a", alertId);
                c.Parameters.AddWithValue("$g", groupValue ?? UngroupedKey);
            },
            cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<AlertInstance?> GetAsync(long instanceId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AlertInstance> rows = await QueryAsync(
            "WHERE instance_id = $id", c => c.Parameters.AddWithValue("$id", instanceId), cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        IReadOnlyList<long> triggers = await TriggerEventIdsAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return rows[0] with { TriggerEventIds = triggers };
    }

    /// <summary>
    /// Opens an instance for a breach, or returns the existing open one (dedup). Returns the
    /// instance id and whether it was newly created — the caller notifies only on creation
    /// (or on the re-notify interval).
    /// </summary>
    public async Task<(long InstanceId, bool Created)> OpenAsync(
        long alertId,
        NotificationLevel severity,
        string? groupValue,
        long observedValue,
        int threshold,
        IReadOnlyList<long> triggerEventIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(nowUtc);
        string group = groupValue ?? UngroupedKey;

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long instanceId;
        bool created;
        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO alert_instances
                  (alert_id, group_value, state, severity, observed_value, threshold, opened_utc, last_notified_utc)
                VALUES ($a, $g, 'firing', $sev, $obs, $th, $now, NULL);
                SELECT last_insert_rowid(), changes();
                """;
            insert.Parameters.AddWithValue("$a", alertId);
            insert.Parameters.AddWithValue("$g", group);
            insert.Parameters.AddWithValue("$sev", AlertJson.SeverityToken(severity));
            insert.Parameters.AddWithValue("$obs", observedValue);
            insert.Parameters.AddWithValue("$th", threshold);
            insert.Parameters.AddWithValue("$now", now);

            await using SqliteDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            long lastRowId = reader.GetInt64(0);
            created = reader.GetInt64(1) == 1;
            instanceId = created ? lastRowId : 0;
        }

        if (!created)
        {
            await using SqliteCommand find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText =
                "SELECT instance_id FROM alert_instances WHERE alert_id = $a AND group_value = $g AND state <> 'resolved';";
            find.Parameters.AddWithValue("$a", alertId);
            find.Parameters.AddWithValue("$g", group);
            instanceId = Convert.ToInt64(
                await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (instanceId, false);
        }

        await AddTransitionAsync(connection, transaction, instanceId, from: null, to: "firing", "alerts-engine", note: null, now, cancellationToken).ConfigureAwait(false);
        await AddTriggerEventsAsync(connection, transaction, instanceId, triggerEventIds, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (instanceId, true);
    }

    public Task<bool> AcknowledgeAsync(long instanceId, string actor, string? note, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        TransitionAsync(
            instanceId,
            fromStates: ["firing"],
            toState: "acknowledged",
            "UPDATE alert_instances SET state = 'acknowledged', acknowledged_utc = $now, acknowledged_by = $actor, note = $note WHERE instance_id = $id AND state = 'firing';",
            actor, note, autoResolved: false, nowUtc, cancellationToken);

    public Task<bool> ResolveAsync(long instanceId, string actor, string? note, bool auto, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        TransitionAsync(
            instanceId,
            fromStates: ["firing", "acknowledged"],
            toState: "resolved",
            "UPDATE alert_instances SET state = 'resolved', resolved_utc = $now, resolved_by = $actor, auto_resolved = $auto, note = COALESCE($note, note) WHERE instance_id = $id AND state <> 'resolved';",
            actor, note, autoResolved: auto, nowUtc, cancellationToken);

    public async Task MarkNotifiedAsync(long instanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE alert_instances SET last_notified_utc = $now WHERE instance_id = $id;";
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(nowUtc));
        command.Parameters.AddWithValue("$id", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AlertInstance>> ListOpenAsync(CancellationToken cancellationToken) =>
        QueryAsync("WHERE state <> 'resolved' ORDER BY opened_utc DESC", null, cancellationToken, appendOrder: false);

    public Task<IReadOnlyList<AlertInstance>> ListForAlertAsync(long alertId, int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE alert_id = $a ORDER BY opened_utc DESC LIMIT $lim",
            c =>
            {
                c.Parameters.AddWithValue("$a", alertId);
                c.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 1000));
            },
            cancellationToken,
            appendOrder: false);

    public Task<IReadOnlyList<AlertInstance>> ListHistoryAsync(AlertHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clauses = new List<string>();
        Action<SqliteCommand> bind = _ => { };

        void Add(string clause, string name, object value)
        {
            clauses.Add(clause);
            Action<SqliteCommand> prev = bind;
            bind = c =>
            {
                prev(c);
                c.Parameters.AddWithValue(name, value);
            };
        }

        if (query.AlertId is { } alertId)
        {
            Add("alert_id = $a", "$a", alertId);
        }

        if (query.State is { } state)
        {
            Add("state = $st", "$st", StateToken(state));
        }

        if (query.OpenOnly)
        {
            clauses.Add("state <> 'resolved'");
        }

        if (query.FromUtc is { } from)
        {
            Add("opened_utc >= $from", "$from", StorageFormat.Timestamp(from));
        }

        if (query.ToUtc is { } to)
        {
            Add("opened_utc < $to", "$to", StorageFormat.Timestamp(to));
        }

        string where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;
        int limit = Math.Clamp(query.Limit, 1, 1000);
        long offset = Math.Max(0, query.Offset);

        return QueryAsync(
            $"{where} ORDER BY opened_utc DESC LIMIT {limit} OFFSET {offset}",
            bind,
            cancellationToken,
            appendOrder: false);
    }

    public async Task<IReadOnlyList<AlertTransition>> ListTransitionsAsync(long instanceId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT from_state, to_state, actor, note, occurred_utc FROM alert_transitions WHERE instance_id = $id ORDER BY transition_id;";
        command.Parameters.AddWithValue("$id", instanceId);

        var list = new List<AlertTransition>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new AlertTransition(
                instanceId,
                reader.IsDBNull(0) ? null : ParseState(reader.GetString(0)),
                ParseState(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                StorageFormat.ParseTimestamp(reader.GetString(4))));
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<NotificationLevel, int>> CountOpenBySeverityAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT severity, COUNT(*) FROM alert_instances WHERE state <> 'resolved' GROUP BY severity;";
        var result = new Dictionary<NotificationLevel, int>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[AlertJson.ParseSeverity(reader.GetString(0))] = (int)reader.GetInt64(1);
        }

        return result;
    }

    public async Task<IReadOnlyList<long>> TriggerEventIdsAsync(long instanceId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT event_id FROM alert_instance_events WHERE instance_id = $id ORDER BY event_id;";
        command.Parameters.AddWithValue("$id", instanceId);
        var ids = new List<long>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task<bool> TransitionAsync(
        long instanceId,
        string[] fromStates,
        string toState,
        string updateSql,
        string actor,
        string? note,
        bool autoResolved,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(nowUtc);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string? currentState;
        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT state FROM alert_instances WHERE instance_id = $id;";
            read.Parameters.AddWithValue("$id", instanceId);
            currentState = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (currentState is null || !fromStates.Contains(currentState))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = updateSql;
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$actor", actor);
            update.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            update.Parameters.AddWithValue("$auto", autoResolved ? 1 : 0);
            update.Parameters.AddWithValue("$id", instanceId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await AddTransitionAsync(connection, transaction, instanceId, currentState, toState, actor, note, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task AddTransitionAsync(
        SqliteConnection connection, SqliteTransaction transaction, long instanceId,
        string? from, string to, string actor, string? note, string now, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO alert_transitions (instance_id, from_state, to_state, actor, note, occurred_utc)
            VALUES ($id, $from, $to, $actor, $note, $now);
            """;
        command.Parameters.AddWithValue("$id", instanceId);
        command.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddTriggerEventsAsync(
        SqliteConnection connection, SqliteTransaction transaction, long instanceId,
        IReadOnlyList<long> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR IGNORE INTO alert_instance_events (instance_id, event_id) VALUES ($id, $ev);";
        command.Parameters.AddWithValue("$id", instanceId);
        SqliteParameter ev = command.Parameters.Add("$ev", SqliteType.Integer);
        foreach (long eventId in eventIds.Distinct().Take(50))
        {
            ev.Value = eventId;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<AlertInstance>> QueryAsync(
        string? whereAndOrder, Action<SqliteCommand>? bind, CancellationToken cancellationToken, bool appendOrder = true)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string suffix = appendOrder ? $"{whereAndOrder} ORDER BY opened_utc DESC" : whereAndOrder ?? string.Empty;
        command.CommandText = $"SELECT {Columns} FROM alert_instances {suffix};";
        bind?.Invoke(command);

        var rows = new List<AlertInstance>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string group = reader.GetString(2);
            rows.Add(new AlertInstance
            {
                InstanceId = reader.GetInt64(0),
                AlertId = reader.GetInt64(1),
                GroupValue = group.Length == 0 ? null : group,
                State = ParseState(reader.GetString(3)),
                Severity = AlertJson.ParseSeverity(reader.GetString(4)),
                ObservedValue = reader.GetInt64(5),
                Threshold = (int)reader.GetInt64(6),
                OpenedUtc = StorageFormat.ParseTimestamp(reader.GetString(7)),
                AcknowledgedUtc = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(8) ? null : reader.GetString(8)),
                AcknowledgedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
                ResolvedUtc = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(10) ? null : reader.GetString(10)),
                ResolvedBy = reader.IsDBNull(11) ? null : reader.GetString(11),
                AutoResolved = reader.GetInt64(12) == 1,
                LastNotifiedUtc = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(13) ? null : reader.GetString(13)),
                Note = reader.IsDBNull(14) ? null : reader.GetString(14),
            });
        }

        return rows;
    }

    private static string StateToken(AlertState state) => state switch
    {
        AlertState.Acknowledged => "acknowledged",
        AlertState.Resolved => "resolved",
        _ => "firing",
    };

    private static AlertState ParseState(string token) => token switch
    {
        "acknowledged" => AlertState.Acknowledged,
        "resolved" => AlertState.Resolved,
        _ => AlertState.Firing,
    };
}
