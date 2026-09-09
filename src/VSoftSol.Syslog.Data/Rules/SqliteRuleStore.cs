using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Streams;

namespace VSoftSol.Syslog.Data.Rules;

/// <summary>A rule as loaded from the <c>rules</c> table (PHASE_07 item 1).</summary>
public sealed record RuleRow
{
    public required long RuleId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public int Priority { get; init; } = 100;

    public ConditionGroup? Filter { get; init; }

    public IReadOnlyList<RuleAction> Actions { get; init; } = [];

    public TimeOfDayWindow? Window { get; init; }

    public IReadOnlyList<long> DeviceGroupIds { get; init; } = [];

    public EscalationPolicy? Escalation { get; init; }

    public bool StopProcessing { get; init; }

    public bool IsSystem { get; init; }

    public long HitCount { get; init; }

    public DateTimeOffset? LastFiredUtc { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Projects the row back to the editable <see cref="RuleDefinition"/>.</summary>
    public RuleDefinition ToDefinition() => new()
    {
        RuleId = RuleId,
        Name = Name,
        Description = Description,
        Enabled = Enabled,
        Priority = Priority,
        Filter = Filter,
        Actions = [.. Actions],
        Window = Window,
        DeviceGroupIds = [.. DeviceGroupIds],
        Escalation = Escalation,
        HitCount = HitCount,
        LastFiredUtc = LastFiredUtc,
        IsSystem = IsSystem,
    };
}

/// <summary>
/// CRUD for the rule set, plus an in-process <see cref="Version"/> counter the ingest-path
/// <c>RuleSetProvider</c> watches (same pattern as <see cref="SqliteStreamStore"/>; ADR 0005
/// makes the single-process counter authoritative).
/// </summary>
public sealed class SqliteRuleStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private const string Columns =
        "rule_id, name, description, enabled, priority, condition_json, actions_json, stop_processing, " +
        "time_window_json, escalation_json, device_group_ids, hit_count, last_fired_utc, is_system, created_utc";

    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private long _version;

    /// <summary>Bumped on every write; the ingest-path provider rebuilds when it changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    public Task<IReadOnlyList<RuleRow>> ListActiveAsync(CancellationToken cancellationToken) =>
        QueryAsync("WHERE enabled = 1", null, cancellationToken);

    public Task<IReadOnlyList<RuleRow>> ListAllAsync(CancellationToken cancellationToken) =>
        QueryAsync(null, null, cancellationToken);

    public async Task<RuleRow?> GetAsync(long ruleId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RuleRow> rows = await QueryAsync(
            "WHERE rule_id = $id", c => c.Parameters.AddWithValue("$id", ruleId), cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<long> CreateAsync(RuleDefinition rule, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rules
              (name, description, enabled, priority, condition_json, actions_json, stop_processing,
               time_window_json, escalation_json, device_group_ids, is_system, created_utc, updated_utc, updated_by)
            VALUES
              ($name, $desc, $enabled, $priority, $cond, $actions, $stop,
               $window, $esc, $groups, 0, $now, $now, $by);
            SELECT last_insert_rowid();
            """;
        Bind(command, rule, updatedBy, now);
        long id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        Interlocked.Increment(ref _version);
        return id;
    }

    public async Task<bool> UpdateAsync(RuleDefinition rule, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Name);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rules SET
              name = CASE WHEN is_system = 1 THEN name ELSE $name END,
              description = $desc, enabled = $enabled, priority = $priority,
              condition_json = $cond, actions_json = $actions, stop_processing = $stop,
              time_window_json = $window, escalation_json = $esc, device_group_ids = $groups,
              updated_utc = $now, updated_by = $by
            WHERE rule_id = $id;
            """;
        Bind(command, rule, updatedBy, now);
        command.Parameters.AddWithValue("$id", rule.RuleId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    public async Task<bool> DeleteAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rules WHERE rule_id = $id AND is_system = 0;";
        command.Parameters.AddWithValue("$id", ruleId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    public async Task<bool> SetEnabledAsync(long ruleId, bool enabled, string updatedBy, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE rules SET enabled = $e, updated_utc = $now, updated_by = $by WHERE rule_id = $id;";
        command.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        command.Parameters.AddWithValue("$id", ruleId);

        bool ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (ok)
        {
            Interlocked.Increment(ref _version);
        }

        return ok;
    }

    /// <summary>
    /// Flushes the in-memory hit counters accumulated by the dispatcher. Does not bump
    /// <see cref="Version"/> — a hit count change must not trigger a rule-set rebuild.
    /// </summary>
    public async Task BumpHitsAsync(IReadOnlyDictionary<long, (long Delta, DateTimeOffset LastFired)> hits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            return;
        }

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE rules SET hit_count = hit_count + $d, last_fired_utc = $t WHERE rule_id = $id;";
        SqliteParameter d = command.Parameters.Add("$d", SqliteType.Integer);
        SqliteParameter t = command.Parameters.Add("$t", SqliteType.Text);
        SqliteParameter id = command.Parameters.Add("$id", SqliteType.Integer);
        foreach ((long ruleId, (long delta, DateTimeOffset lastFired)) in hits)
        {
            d.Value = delta;
            t.Value = StorageFormat.Timestamp(lastFired);
            id.Value = ruleId;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, RuleDefinition rule, string updatedBy, string now)
    {
        command.Parameters.AddWithValue("$name", rule.Name.Trim());
        command.Parameters.AddWithValue("$desc", (object?)rule.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$priority", rule.Priority);
        string conditionJson = StreamMatchJson.Serialize(rule.Filter);
        command.Parameters.AddWithValue("$cond", conditionJson.Length > 0 ? conditionJson : DBNull.Value);
        command.Parameters.AddWithValue("$actions", RuleJson.SerializeActions(rule.Actions));
        command.Parameters.AddWithValue("$stop", rule.Actions.Any(a => a is Core.Rules.SuppressAction) ? 1 : 0);
        command.Parameters.AddWithValue("$window", (object?)RuleJson.SerializeWindow(rule.Window) ?? DBNull.Value);
        command.Parameters.AddWithValue("$esc", (object?)RuleJson.SerializeEscalation(rule.Escalation) ?? DBNull.Value);
        command.Parameters.AddWithValue("$groups", (object?)RuleJson.SerializeGroupIds(rule.DeviceGroupIds) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
    }

    private async Task<IReadOnlyList<RuleRow>> QueryAsync(
        string? where, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM rules {where} ORDER BY priority, rule_id;";
        bind?.Invoke(command);

        var rows = new List<RuleRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ConditionNode? filter = StreamMatchJson.Deserialize(reader.IsDBNull(5) ? null : reader.GetString(5));
            rows.Add(new RuleRow
            {
                RuleId = reader.GetInt64(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Enabled = reader.GetInt64(3) == 1,
                Priority = (int)reader.GetInt64(4),
                Filter = filter as ConditionGroup,
                Actions = RuleJson.DeserializeActions(reader.IsDBNull(6) ? null : reader.GetString(6)),
                StopProcessing = reader.GetInt64(7) == 1,
                Window = RuleJson.DeserializeWindow(reader.IsDBNull(8) ? null : reader.GetString(8)),
                Escalation = RuleJson.DeserializeEscalation(reader.IsDBNull(9) ? null : reader.GetString(9)),
                DeviceGroupIds = RuleJson.DeserializeGroupIds(reader.IsDBNull(10) ? null : reader.GetString(10)),
                HitCount = reader.GetInt64(11),
                LastFiredUtc = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(12) ? null : reader.GetString(12)),
                IsSystem = reader.GetInt64(13) == 1,
                CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(14)),
            });
        }

        return rows;
    }
}
