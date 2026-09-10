using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Alerts;

/// <summary>
/// Fetches the window data an alert evaluation needs (ADR 0016, the hybrid model):
/// <list type="bullet">
///   <item>no filter → a parameterised <c>GROUP BY</c> aggregate straight off the
///     <c>received_utc</c> index — scales to millions of rows;</item>
///   <item>with a <c>ConditionGroup</c> filter → <see cref="StreamWindowAsync"/> hands the
///     caller (the scheduler, which owns the compiled condition) a capped stream of the
///     windowed events to filter and group in memory;</item>
///   <item>device-silent → each device's last-seen timestamp, straight from the
///     <c>devices</c> registry.</item>
/// </list>
/// Every value is a bound parameter (SECURITY_STANDARDS §5.1); the group-by column is
/// resolved against a fixed allow-list, never interpolated from user input.
/// </summary>
public sealed class SqliteAlertWindowReader(SqliteConnectionFactory factory)
{
    /// <summary>Core columns an alert may group counts by. <c>message</c> is intentionally absent.</summary>
    private static readonly Dictionary<string, string> GroupableColumns = new(StringComparer.Ordinal)
    {
        ["hostname"] = "hostname",
        ["source_ip"] = "source_ip",
        ["app"] = "app_name",
        ["proc_id"] = "proc_id",
        ["msg_id"] = "msg_id",
        ["severity"] = "severity",
        ["facility"] = "facility",
        ["vendor"] = "vendor",
        ["protocol"] = "protocol",
        ["parse_status"] = "parse_status",
    };

    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>True when <paramref name="field"/> can be grouped by a pure SQL aggregate.</summary>
    public static bool IsSqlGroupable(string? field) =>
        field is not null && GroupableColumns.ContainsKey(field);

    /// <summary>
    /// Threshold / Absence, no filter: the match count per group value in
    /// <c>[fromUtc, toUtc)</c>. <paramref name="groupByField"/> null ⇒ a single ungrouped
    /// count. <paramref name="sampleSize"/> event ids are collected per group for the
    /// triggering-event record.
    /// </summary>
    public async Task<IReadOnlyList<GroupCount>> CountByGroupAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? groupByField,
        IReadOnlyList<long> deviceIds,
        IReadOnlyList<long> streamIds,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        string? column = null;
        if (groupByField is not null && !GroupableColumns.TryGetValue(groupByField, out column))
        {
            throw new ArgumentException($"'{groupByField}' cannot be grouped by a SQL aggregate.", nameof(groupByField));
        }

        (string where, string streamJoin) = BuildScope(command, fromUtc, toUtc, deviceIds, streamIds);
        string groupExpr = column is null ? "''" : $"CAST(COALESCE(e.{column}, '(none)') AS TEXT)";

        command.CommandText = $"""
            SELECT {groupExpr} AS grp, COUNT(*) AS n
            FROM events e {streamJoin}
            {where}
            GROUP BY grp;
            """;

        var groups = new List<(string? Group, long Count)>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string g = reader.GetString(0);
                groups.Add((column is null ? null : g, reader.GetInt64(1)));
            }
        }

        var result = new List<GroupCount>(groups.Count);
        foreach ((string? group, long count) in groups)
        {
            IReadOnlyList<long> sample = sampleSize > 0
                ? await SampleEventIdsAsync(connection, fromUtc, toUtc, column, group, deviceIds, streamIds, sampleSize, cancellationToken).ConfigureAwait(false)
                : [];
            result.Add(new GroupCount(group, count, sample));
        }

        return result;
    }

    /// <summary>DistinctCount, no filter: the number of distinct values of the group-by column.</summary>
    public async Task<(long Distinct, IReadOnlyList<long> Sample)> DistinctCountAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string groupByField,
        IReadOnlyList<long> deviceIds,
        IReadOnlyList<long> streamIds,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        if (!GroupableColumns.TryGetValue(groupByField, out string? column))
        {
            throw new ArgumentException($"'{groupByField}' cannot be counted by a SQL aggregate.", nameof(groupByField));
        }

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        (string where, string streamJoin) = BuildScope(command, fromUtc, toUtc, deviceIds, streamIds);
        command.CommandText = $"SELECT COUNT(DISTINCT e.{column}) FROM events e {streamJoin} {where};";

        long distinct = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        IReadOnlyList<long> sample = sampleSize > 0
            ? await SampleEventIdsAsync(connection, fromUtc, toUtc, groupByColumn: null, groupValue: null, deviceIds, streamIds, sampleSize, cancellationToken).ConfigureAwait(false)
            : [];

        return (distinct, sample);
    }

    /// <summary>
    /// The filtered path: streams every event in <c>[fromUtc, toUtc)</c> in scope, oldest
    /// first, up to <paramref name="cap"/>. The caller applies the compiled
    /// <c>ConditionGroup</c> in memory and groups the survivors.
    /// </summary>
    public async IAsyncEnumerable<SyslogEvent> StreamWindowAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<long> deviceIds,
        IReadOnlyList<long> streamIds,
        int cap,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection fieldConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        (string where, string streamJoin) = BuildScope(command, fromUtc, toUtc, deviceIds, streamIds);
        command.CommandText =
            $"SELECT {EventRowMapper.Prefixed("e")} FROM events e {streamJoin} {where} " +
            "ORDER BY e.received_utc, e.event_id LIMIT $cap;";
        command.Parameters.AddWithValue("$cap", Math.Max(1, cap));

        var batch = new List<SyslogEvent>(256);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(EventRowMapper.Read(reader));
            if (batch.Count == 256)
            {
                foreach (SyslogEvent e in await HydrateAsync(fieldConnection, batch, cancellationToken).ConfigureAwait(false))
                {
                    yield return e;
                }

                batch.Clear();
            }
        }

        foreach (SyslogEvent e in await HydrateAsync(fieldConnection, batch, cancellationToken).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    /// <summary>
    /// The "would have fired" preview (PHASE_08 UX gate). Buckets the events of the last
    /// <paramref name="lookback"/> into <paramref name="windowSeconds"/>-sized slots and returns
    /// the per-(bucket, group) counts, so the caller can count how many buckets would have
    /// breached — one query, no per-window round trip. Only the no-filter path.
    /// </summary>
    public async Task<IReadOnlyList<(long Bucket, string? Group, long Count)>> PreviewBucketCountsAsync(
        DateTimeOffset nowUtc,
        TimeSpan lookback,
        int windowSeconds,
        string? groupByField,
        IReadOnlyList<long> deviceIds,
        IReadOnlyList<long> streamIds,
        CancellationToken cancellationToken)
    {
        string? column = null;
        if (groupByField is not null && !GroupableColumns.TryGetValue(groupByField, out column))
        {
            throw new ArgumentException($"'{groupByField}' cannot be grouped by a SQL aggregate.", nameof(groupByField));
        }

        DateTimeOffset from = nowUtc - lookback;
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        (string where, string streamJoin) = BuildScope(command, from, nowUtc, deviceIds, streamIds);
        command.Parameters.AddWithValue("$w", windowSeconds);
        string groupExpr = column is null ? "''" : $"CAST(COALESCE(e.{column}, '(none)') AS TEXT)";

        // Integer-seconds bucketing: strftime('%s') is whole-second unix time, so the bucket
        // index is exact — no float rounding at a window boundary.
        command.CommandText = $"""
            SELECT (CAST(strftime('%s', e.received_utc) AS INTEGER) - CAST(strftime('%s', $from) AS INTEGER)) / $w AS bucket,
                   {groupExpr} AS grp,
                   COUNT(*) AS n
            FROM events e {streamJoin}
            {where}
            GROUP BY bucket, grp;
            """;

        var rows = new List<(long, string?, long)>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetInt64(0), column is null ? null : reader.GetString(1), reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>DeviceSilent: the last-seen timestamp of every device in scope.</summary>
    public async Task<IReadOnlyList<DeviceSilence>> DeviceLastSeenAsync(
        IReadOnlyList<long> deviceGroupIds, int fallbackThresholdMinutes, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        string filter = "WHERE d.approval_status = 'approved' AND d.is_enabled = 1";
        if (deviceGroupIds.Count > 0)
        {
            string list = BindLongList(command, "g", deviceGroupIds);
            filter += $" AND d.device_id IN (SELECT device_id FROM device_group_members WHERE group_id IN ({list}))";
        }

        command.CommandText = $"""
            SELECT d.device_id, d.name, d.heartbeat_minutes,
                   COALESCE((SELECT MAX(e.received_utc) FROM events e WHERE e.device_id = d.device_id), d.last_seen_utc)
            FROM devices d
            {filter};
            """;

        var result = new List<DeviceSilence>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int threshold = reader.IsDBNull(2) ? fallbackThresholdMinutes : (int)reader.GetInt64(2);
            DateTimeOffset? lastSeen = StorageFormat.ParseTimestampOrNull(reader.IsDBNull(3) ? null : reader.GetString(3));
            result.Add(new DeviceSilence(reader.GetInt64(0), reader.GetString(1), lastSeen, Math.Max(1, threshold)));
        }

        return result;
    }

    /// <summary>Resolves the alert's device-group restriction to a concrete device-id set (for the SQL scope).</summary>
    public async Task<IReadOnlyList<long>> ResolveDeviceIdsAsync(
        IReadOnlyList<long> deviceGroupIds, CancellationToken cancellationToken)
    {
        if (deviceGroupIds.Count == 0)
        {
            return [];
        }

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string list = BindLongList(command, "g", deviceGroupIds);
        command.CommandText = $"SELECT DISTINCT device_id FROM device_group_members WHERE group_id IN ({list});";

        var ids = new List<long>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<long>> SampleEventIdsAsync(
        SqliteConnection connection,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? groupByColumn,
        string? groupValue,
        IReadOnlyList<long> deviceIds,
        IReadOnlyList<long> streamIds,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        (string where, string streamJoin) = BuildScope(command, fromUtc, toUtc, deviceIds, streamIds);
        string groupClause = string.Empty;
        if (groupByColumn is not null)
        {
            groupClause = groupValue == "(none)" || groupValue is null
                ? $" AND e.{groupByColumn} IS NULL"
                : $" AND e.{groupByColumn} = $grp";
            if (groupValue is not null && groupValue != "(none)")
            {
                command.Parameters.AddWithValue("$grp", groupValue);
            }
        }

        command.CommandText =
            $"SELECT e.event_id FROM events e {streamJoin} {where}{groupClause} " +
            "ORDER BY e.event_id DESC LIMIT $sample;";
        command.Parameters.AddWithValue("$sample", Math.Max(1, sampleSize));

        var ids = new List<long>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        ids.Reverse();
        return ids;
    }

    private static (string Where, string StreamJoin) BuildScope(
        SqliteCommand command,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<long> deviceIds,
        IReadOnlyList<long> streamIds)
    {
        var clauses = new List<string> { "e.received_utc >= $from", "e.received_utc < $to" };
        command.Parameters.AddWithValue("$from", StorageFormat.Timestamp(fromUtc));
        command.Parameters.AddWithValue("$to", StorageFormat.Timestamp(toUtc));

        if (deviceIds.Count > 0)
        {
            clauses.Add($"e.device_id IN ({BindLongList(command, "d", deviceIds)})");
        }

        string streamJoin = string.Empty;
        if (streamIds.Count > 0)
        {
            clauses.Add($"EXISTS (SELECT 1 FROM event_streams es WHERE es.event_id = e.event_id " +
                        $"AND es.stream_id IN ({BindLongList(command, "s", streamIds)}))");
        }

        return ("WHERE " + string.Join(" AND ", clauses), streamJoin);
    }

    private static string BindLongList(SqliteCommand command, string prefix, IReadOnlyList<long> values)
    {
        var names = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            string name = $"${prefix}{i}";
            command.Parameters.AddWithValue(name, values[i]);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    private static async Task<IReadOnlyList<SyslogEvent>> HydrateAsync(
        SqliteConnection fieldConnection, List<SyslogEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return [];
        }

        Dictionary<long, IReadOnlyList<EventField>> fields = await EventRowMapper
            .LoadFieldsAsync(fieldConnection, batch.Select(e => e.EventId).ToList(), cancellationToken)
            .ConfigureAwait(false);

        var result = new List<SyslogEvent>(batch.Count);
        foreach (SyslogEvent e in batch)
        {
            result.Add(fields.TryGetValue(e.EventId, out IReadOnlyList<EventField>? list) && list.Count > 0
                ? EventRowMapper.WithFields(e, list)
                : e);
        }

        return result;
    }
}
