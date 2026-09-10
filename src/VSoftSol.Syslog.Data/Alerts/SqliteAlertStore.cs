using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Alerts;

/// <summary>The evaluation checkpoint for one alert (<c>alert_eval_runs</c>).</summary>
public sealed record AlertEvalRun(
    DateTimeOffset? LastWindowEndUtc,
    DateTimeOffset? LastRunUtc,
    string? LastStatus,
    int ConsecutiveFailures);

/// <summary>An alert as loaded from the <c>alert_definitions</c> table (PHASE_08 item 1).</summary>
public sealed record AlertRow
{
    public required long AlertId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public NotificationLevel Severity { get; init; } = NotificationLevel.Warning;

    public bool Enabled { get; init; } = true;

    public AlertEvaluationType Type { get; init; }

    public Core.Conditions.ConditionGroup? Filter { get; init; }

    public int WindowSeconds { get; init; }

    public int IntervalSeconds { get; init; }

    public string? GroupByField { get; init; }

    public int Threshold { get; init; }

    public string? RemediationNotes { get; init; }

    public IReadOnlyList<RuleAction> Actions { get; init; } = [];

    public IReadOnlyList<long> DeviceGroupIds { get; init; } = [];

    public IReadOnlyList<long> StreamIds { get; init; } = [];

    public int ReNotifySeconds { get; init; }

    public bool AutoResolve { get; init; }

    public bool IsSystem { get; init; }

    public long HitCount { get; init; }

    public DateTimeOffset? LastEvaluatedUtc { get; init; }

    public DateTimeOffset? LastFiredUtc { get; init; }

    public AlertDefinition ToDefinition() => new()
    {
        AlertId = AlertId,
        Name = Name,
        Description = Description,
        Severity = Severity,
        Enabled = Enabled,
        Type = Type,
        Filter = Filter,
        WindowSeconds = WindowSeconds,
        IntervalSeconds = IntervalSeconds,
        GroupByField = GroupByField,
        Threshold = Threshold,
        RemediationNotes = RemediationNotes,
        Actions = [.. Actions],
        DeviceGroupIds = [.. DeviceGroupIds],
        StreamIds = [.. StreamIds],
        ReNotifySeconds = ReNotifySeconds,
        AutoResolve = AutoResolve,
        IsSystem = IsSystem,
        HitCount = HitCount,
        LastEvaluatedUtc = LastEvaluatedUtc,
        LastFiredUtc = LastFiredUtc,
    };
}

/// <summary>
/// CRUD for alert definitions, plus an in-process <see cref="Version"/> counter the
/// scheduler's <c>AlertSetProvider</c> watches (the same pattern as
/// <see cref="Rules.SqliteRuleStore"/>). ADR 0005 (one process) makes the counter
/// authoritative.
/// </summary>
public sealed class SqliteAlertStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private const string Columns =
        "alert_id, name, description, severity, enabled, eval_type, filter_json, window_seconds, interval_seconds, " +
        "group_by_field, threshold, remediation_notes, actions_json, device_group_ids, stream_ids, renotify_seconds, " +
        "auto_resolve, is_system, hit_count, last_evaluated_utc, last_fired_utc";

    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private long _version;

    /// <summary>Bumped on every write; the scheduler's provider rebuilds when it changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    public Task<IReadOnlyList<AlertRow>> ListActiveAsync(CancellationToken cancellationToken) =>
        QueryAsync("WHERE enabled = 1", null, cancellationToken);

    public Task<IReadOnlyList<AlertRow>> ListAllAsync(CancellationToken cancellationToken) =>
        QueryAsync(null, null, cancellationToken);

    public async Task<AlertRow?> GetAsync(long alertId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AlertRow> rows = await QueryAsync(
            "WHERE alert_id = $id", c => c.Parameters.AddWithValue("$id", alertId), cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<long> CreateAsync(AlertDefinition alert, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentException.ThrowIfNullOrWhiteSpace(alert.Name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_definitions
              (name, description, severity, enabled, eval_type, filter_json, window_seconds, interval_seconds,
               group_by_field, threshold, remediation_notes, actions_json, device_group_ids, stream_ids,
               renotify_seconds, auto_resolve, is_system, created_utc, updated_utc, updated_by)
            VALUES
              ($name, $desc, $sev, $enabled, $type, $filter, $window, $interval,
               $groupby, $threshold, $remediation, $actions, $groups, $streams,
               $renotify, $autoresolve, 0, $now, $now, $by);
            SELECT last_insert_rowid();
            """;
        Bind(command, alert, updatedBy, now);
        long id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        Interlocked.Increment(ref _version);
        return id;
    }

    public async Task<bool> UpdateAsync(AlertDefinition alert, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentException.ThrowIfNullOrWhiteSpace(alert.Name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_definitions SET
              name = CASE WHEN is_system = 1 THEN name ELSE $name END,
              description = $desc, severity = $sev, enabled = $enabled, eval_type = $type,
              filter_json = $filter, window_seconds = $window, interval_seconds = $interval,
              group_by_field = $groupby, threshold = $threshold, remediation_notes = $remediation,
              actions_json = $actions, device_group_ids = $groups, stream_ids = $streams,
              renotify_seconds = $renotify, auto_resolve = $autoresolve,
              updated_utc = $now, updated_by = $by
            WHERE alert_id = $id;
            """;
        Bind(command, alert, updatedBy, now);
        command.Parameters.AddWithValue("$id", alert.AlertId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    public async Task<bool> DeleteAsync(long alertId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_definitions WHERE alert_id = $id AND is_system = 0;";
        command.Parameters.AddWithValue("$id", alertId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    public async Task<bool> SetEnabledAsync(long alertId, bool enabled, string updatedBy, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE alert_definitions SET enabled = $e, updated_utc = $now, updated_by = $by WHERE alert_id = $id;";
        command.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        command.Parameters.AddWithValue("$id", alertId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    /// <summary>
    /// Records an evaluation firing: bumps the hit counter and stamps <c>last_fired_utc</c>.
    /// Does not bump <see cref="Version"/> — a hit must not trigger a rebuild.
    /// </summary>
    public async Task RecordFiredAsync(long alertId, DateTimeOffset firedUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE alert_definitions SET hit_count = hit_count + 1, last_fired_utc = $t WHERE alert_id = $id;";
        command.Parameters.AddWithValue("$t", StorageFormat.Timestamp(firedUtc));
        command.Parameters.AddWithValue("$id", alertId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stamps <c>last_evaluated_utc</c> after an evaluation pass. Does not bump <see cref="Version"/>.</summary>
    public async Task RecordEvaluatedAsync(long alertId, DateTimeOffset evaluatedUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE alert_definitions SET last_evaluated_utc = $t WHERE alert_id = $id;";
        command.Parameters.AddWithValue("$t", StorageFormat.Timestamp(evaluatedUtc));
        command.Parameters.AddWithValue("$id", alertId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The evaluation checkpoint for one alert (PHASE_08 item 3 — survives restart).</summary>
    public async Task<AlertEvalRun?> GetEvalRunAsync(long alertId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT last_window_end_utc, last_run_utc, last_status, consecutive_failures FROM alert_eval_runs WHERE alert_id = $id;";
        command.Parameters.AddWithValue("$id", alertId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AlertEvalRun(
            StorageFormat.ParseTimestampOrNull(reader.IsDBNull(0) ? null : reader.GetString(0)),
            StorageFormat.ParseTimestampOrNull(reader.IsDBNull(1) ? null : reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            (int)reader.GetInt64(3));
    }

    public async Task RecordEvalRunAsync(
        long alertId, DateTimeOffset windowEndUtc, DateTimeOffset runUtc, string status, bool failed, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_eval_runs (alert_id, last_window_end_utc, last_run_utc, last_status, consecutive_failures)
            VALUES ($id, $we, $run, $status, $fail)
            ON CONFLICT(alert_id) DO UPDATE SET
              last_window_end_utc = excluded.last_window_end_utc,
              last_run_utc = excluded.last_run_utc,
              last_status = excluded.last_status,
              consecutive_failures = CASE WHEN $fail = 1 THEN alert_eval_runs.consecutive_failures + 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("$id", alertId);
        command.Parameters.AddWithValue("$we", StorageFormat.Timestamp(windowEndUtc));
        command.Parameters.AddWithValue("$run", StorageFormat.Timestamp(runUtc));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$fail", failed ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, AlertDefinition alert, string updatedBy, string now)
    {
        command.Parameters.AddWithValue("$name", alert.Name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)alert.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$sev", AlertJson.SeverityToken(alert.Severity));
        command.Parameters.AddWithValue("$enabled", alert.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$type", AlertJson.EvalTypeToken(alert.Type));
        string filterJson = AlertJson.SerializeFilter(alert.Filter);
        command.Parameters.AddWithValue("$filter", filterJson.Length > 0 ? filterJson : DBNull.Value);
        command.Parameters.AddWithValue("$window", alert.WindowSeconds);
        command.Parameters.AddWithValue("$interval", alert.IntervalSeconds);
        command.Parameters.AddWithValue("$groupby", (object?)alert.GroupByField ?? DBNull.Value);
        command.Parameters.AddWithValue("$threshold", alert.Threshold);
        command.Parameters.AddWithValue("$remediation", (object?)alert.RemediationNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$actions", AlertJson.SerializeActions(alert.Actions));
        command.Parameters.AddWithValue("$groups", (object?)AlertJson.SerializeIds(alert.DeviceGroupIds) ?? DBNull.Value);
        command.Parameters.AddWithValue("$streams", (object?)AlertJson.SerializeIds(alert.StreamIds) ?? DBNull.Value);
        command.Parameters.AddWithValue("$renotify", alert.ReNotifySeconds);
        command.Parameters.AddWithValue("$autoresolve", alert.AutoResolve ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
    }

    private async Task<IReadOnlyList<AlertRow>> QueryAsync(
        string? where, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM alert_definitions {where} ORDER BY alert_id;";
        bind?.Invoke(command);

        var rows = new List<AlertRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AlertRow
            {
                AlertId = reader.GetInt64(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Severity = AlertJson.ParseSeverity(reader.GetString(3)),
                Enabled = reader.GetInt64(4) == 1,
                Type = AlertJson.ParseEvalType(reader.GetString(5)),
                Filter = AlertJson.DeserializeFilter(reader.IsDBNull(6) ? null : reader.GetString(6)),
                WindowSeconds = (int)reader.GetInt64(7),
                IntervalSeconds = (int)reader.GetInt64(8),
                GroupByField = reader.IsDBNull(9) ? null : reader.GetString(9),
                Threshold = (int)reader.GetInt64(10),
                RemediationNotes = reader.IsDBNull(11) ? null : reader.GetString(11),
                Actions = AlertJson.DeserializeActions(reader.IsDBNull(12) ? null : reader.GetString(12)),
                DeviceGroupIds = AlertJson.DeserializeIds(reader.IsDBNull(13) ? null : reader.GetString(13)),
                StreamIds = AlertJson.DeserializeIds(reader.IsDBNull(14) ? null : reader.GetString(14)),
                ReNotifySeconds = (int)reader.GetInt64(15),
                AutoResolve = reader.GetInt64(16) == 1,
                IsSystem = reader.GetInt64(17) == 1,
                HitCount = reader.GetInt64(18),
                LastEvaluatedUtc = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(19) ? null : reader.GetString(19)),
                LastFiredUtc = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(20) ? null : reader.GetString(20)),
            });
        }

        return rows;
    }
}
