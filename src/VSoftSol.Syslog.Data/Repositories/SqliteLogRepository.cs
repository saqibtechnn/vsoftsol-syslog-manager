using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="ILogRepository"/>. Writes go through batched transactions
/// under the connection factory's single-writer lock; reads run concurrently on their own
/// WAL connections. Every parameter is bound — no string-built SQL (SECURITY_STANDARDS.md §5.1).
/// </summary>
public sealed class SqliteLogRepository : ILogRepository
{
    private const string EventColumns =
        "event_id, received_utc, event_utc, source_ip, hostname, app_name, proc_id, msg_id, " +
        "facility, severity, protocol, listener_id, message, raw_message, parse_status, " +
        "occurrence_count, structured_data_json, device_id, vendor";

    private const string InsertEventSql = """
        INSERT INTO events
          (received_utc, event_utc, source_ip, hostname, app_name, proc_id, msg_id, facility, severity,
           protocol, listener_id, message, raw_message, search_text, parse_status, occurrence_count,
           structured_data_json, device_id, vendor)
        VALUES
          ($received_utc, $event_utc, $source_ip, $hostname, $app_name, $proc_id, $msg_id, $facility, $severity,
           $protocol, $listener_id, $message, $raw_message, $search_text, $parse_status, $occurrence_count,
           $structured_data_json, $device_id, $vendor);
        """;

    private readonly SqliteConnectionFactory _factory;
    private readonly int _batchSize;
    private readonly int _retentionChunk;

    public SqliteLogRepository(SqliteConnectionFactory factory, IOptions<SqliteDataOptions> options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentNullException.ThrowIfNull(options);
        _batchSize = Math.Max(1, options.Value.InsertBatchSize);
        _retentionChunk = Math.Max(100, options.Value.RetentionDeleteChunk);
    }

    public async Task<long> AppendAsync(SyslogEvent syslogEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(syslogEvent);
        IReadOnlyList<long> ids = await AppendBatchAsync([syslogEvent], cancellationToken).ConfigureAwait(false);
        return ids[0];
    }

    public async Task<IReadOnlyList<long>> AppendBatchAsync(
        IReadOnlyCollection<SyslogEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return [];
        }

        SyslogEvent[] all = events as SyslogEvent[] ?? events.ToArray();
        var assignedIds = new long[all.Length];

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Commands and their prepared statements are built once and reused across every
        // chunk transaction — the per-chunk cost is then just BEGIN/COMMIT.
        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = InsertEventSql;
        EventParameters parameters = EventParameters.AddTo(insert);

        await using SqliteCommand insertField = connection.CreateCommand();
        insertField.CommandText = "INSERT INTO event_fields (event_id, name, value) VALUES ($event_id, $name, $value);";
        SqliteParameter fieldEventId = insertField.Parameters.Add("$event_id", SqliteType.Integer);
        SqliteParameter fieldName = insertField.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter fieldValue = insertField.Parameters.Add("$value", SqliteType.Text);

        await using SqliteCommand lastRowId = connection.CreateCommand();
        lastRowId.CommandText = "SELECT last_insert_rowid();";

        // Stream routing (PHASE_06) is decided upstream by the ingest enricher and arrives on
        // SyslogEvent.StreamIds; the repository just persists it, in the same transaction as
        // the event. The parse-failure link stays as a hard-wired safety net so a routing
        // bug can never orphan a raw event from the "Parse Failures" stream (PHASE_03 item 3).
        await using SqliteCommand linkStream = connection.CreateCommand();
        linkStream.CommandText =
            "INSERT OR IGNORE INTO event_streams (event_id, stream_id) VALUES ($event_id, $stream_id);";
        SqliteParameter linkStreamEventId = linkStream.Parameters.Add("$event_id", SqliteType.Integer);
        SqliteParameter linkStreamId = linkStream.Parameters.Add("$stream_id", SqliteType.Integer);

        await using SqliteCommand linkParseFailure = connection.CreateCommand();
        linkParseFailure.CommandText = """
            INSERT OR IGNORE INTO event_streams (event_id, stream_id)
                SELECT $event_id, stream_id FROM streams WHERE name = 'Parse Failures';
            """;
        SqliteParameter linkEventId = linkParseFailure.Parameters.Add("$event_id", SqliteType.Integer);

        // FTS is NOT written here — SearchIndexMaintainer copies new rows into events_fts
        // in the background. A per-row AFTER INSERT trigger (or an inline per-row FTS
        // insert) is ~10x slower and cannot meet the insert-throughput gate. See ADR 0009.
        for (int offset = 0; offset < all.Length; offset += _batchSize)
        {
            int count = Math.Min(_batchSize, all.Length - offset);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                insert.Transaction = transaction;
                insertField.Transaction = transaction;
                lastRowId.Transaction = transaction;
                linkStream.Transaction = transaction;
                linkParseFailure.Transaction = transaction;

                for (int i = 0; i < count; i++)
                {
                    parameters.Bind(all[offset + i]);
                    insert.ExecuteNonQuery();
                }

                // event_id is AUTOINCREMENT and this transaction only appends, so the
                // rows just written have contiguous ids ending at last_insert_rowid().
                // One scalar call per chunk instead of one per row.
                long lastId = (long)lastRowId.ExecuteScalar()!;
                long firstId = lastId - count + 1;

                for (int i = 0; i < count; i++)
                {
                    long id = firstId + i;
                    assignedIds[offset + i] = id;
                    SyslogEvent evt = all[offset + i];

                    if (evt.StreamIds.Count > 0)
                    {
                        linkStreamEventId.Value = id;
                        foreach (long streamId in evt.StreamIds)
                        {
                            linkStreamId.Value = streamId;
                            linkStream.ExecuteNonQuery();
                        }
                    }

                    if (evt.ParseStatus == ParseStatus.Raw)
                    {
                        linkEventId.Value = id;
                        linkParseFailure.ExecuteNonQuery();
                    }

                    IReadOnlyList<EventField> fields = evt.Fields;
                    if (fields.Count == 0)
                    {
                        continue;
                    }

                    fieldEventId.Value = id;
                    foreach (EventField field in fields)
                    {
                        fieldName.Value = StorageFormat.SanitizeText(field.Name);
                        fieldValue.Value = StorageFormat.SanitizeText(field.Value);
                        insertField.ExecuteNonQuery();
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return assignedIds;
    }

    public async Task IncrementOccurrenceAsync(
        IReadOnlyDictionary<long, int> increments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(increments);
        if (increments.Count == 0)
        {
            return;
        }

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand bump = connection.CreateCommand();
            bump.Transaction = transaction;
            bump.CommandText =
                "UPDATE events SET occurrence_count = occurrence_count + $by WHERE event_id = $id;";
            SqliteParameter byParam = bump.Parameters.Add("$by", SqliteType.Integer);
            SqliteParameter idParam = bump.Parameters.Add("$id", SqliteType.Integer);

            foreach ((long id, int by) in increments)
            {
                if (by <= 0)
                {
                    continue;
                }

                idParam.Value = id;
                byParam.Value = by;
                await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Copies up to <paramref name="maxRows"/> not-yet-indexed events into the FTS index
    /// and advances the watermark. Returns the number indexed. Called on a short interval
    /// by <c>SearchIndexMaintainer</c>; tests call it directly. Runs under the write lock.
    /// </summary>
    public async Task<int> SyncSearchIndexAsync(int maxRows, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long watermark;
            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT last_indexed_event_id FROM fts_state WHERE id = 1;";
                watermark = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            // Resolve the window [watermark+1 .. upperBound] first, then index it. Splitting
            // the count/bound query from the INSERT avoids any multi-statement reader ambiguity.
            int indexed;
            long upperBound;
            await using (SqliteCommand window = connection.CreateCommand())
            {
                window.Transaction = transaction;
                window.CommandText = """
                    SELECT COUNT(*), COALESCE(MAX(event_id), $wm)
                    FROM (SELECT event_id FROM events WHERE event_id > $wm ORDER BY event_id LIMIT $max);
                    """;
                window.Parameters.AddWithValue("$wm", watermark);
                window.Parameters.AddWithValue("$max", maxRows);
                await using SqliteDataReader reader = await window.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                indexed = reader.GetInt32(0);
                upperBound = reader.GetInt64(1);
            }

            if (indexed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            await using (SqliteCommand copy = connection.CreateCommand())
            {
                copy.Transaction = transaction;
                copy.CommandText = """
                    INSERT INTO events_fts (rowid, search_text)
                        SELECT event_id, search_text FROM events
                        WHERE event_id > $wm AND event_id <= $upper;
                    """;
                copy.Parameters.AddWithValue("$wm", watermark);
                copy.Parameters.AddWithValue("$upper", upperBound);
                await copy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand advance = connection.CreateCommand())
            {
                advance.Transaction = transaction;
                advance.CommandText = "UPDATE fts_state SET last_indexed_event_id = $wm WHERE id = 1;";
                advance.Parameters.AddWithValue("$wm", upperBound);
                await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return indexed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SyslogEvent?> GetByIdAsync(long eventId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        SyslogEvent? evt;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {EventColumns} FROM events WHERE event_id = $id;";
            command.Parameters.AddWithValue("$id", eventId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            evt = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? EventReader.Read(reader) : null;
        }

        if (evt is null)
        {
            return null;
        }

        Dictionary<long, IReadOnlyList<EventField>> map =
            await LoadFieldsAsync(connection, [eventId], cancellationToken).ConfigureAwait(false);
        return map.TryGetValue(eventId, out IReadOnlyList<EventField>? fields) && fields.Count > 0
            ? CloneWithFields(evt, fields)
            : evt;
    }

    public async IAsyncEnumerable<SyslogEvent> QueryAsync(
        LogQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        var sql = new StringBuilder($"SELECT {Prefixed(EventColumns, "e")} FROM events e");
        AppendFilter(command, sql, query, forCount: false);
        sql.Append(query.Descending
            ? " ORDER BY e.received_utc DESC, e.event_id DESC"
            : " ORDER BY e.received_utc ASC, e.event_id ASC");
        sql.Append(" LIMIT $limit OFFSET $offset;");
        command.Parameters.AddWithValue("$limit", Math.Max(0, query.Limit));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        command.CommandText = sql.ToString();

        var page = new List<SyslogEvent>(Math.Min(query.Limit, 1024));
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                page.Add(EventReader.Read(reader));
            }
        }

        if (page.Count == 0)
        {
            yield break;
        }

        Dictionary<long, IReadOnlyList<EventField>> fieldMap =
            await LoadFieldsAsync(connection, page.Select(e => e.EventId).ToList(), cancellationToken).ConfigureAwait(false);

        foreach (SyslogEvent evt in page)
        {
            yield return fieldMap.TryGetValue(evt.EventId, out IReadOnlyList<EventField>? fields) && fields.Count > 0
                ? CloneWithFields(evt, fields)
                : evt;
        }
    }

    public async Task<long> CountAsync(LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        var sql = new StringBuilder("SELECT COUNT(*) FROM events e");
        AppendFilter(command, sql, query, forCount: true);
        command.CommandText = sql.ToString();

        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<SyslogEvent>> GetContextAsync(
        long eventId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(before);
        ArgumentOutOfRangeException.ThrowIfNegative(after);

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        (string sourceIp, string receivedUtc)? anchor = await ReadAnchorAsync(connection, eventId, cancellationToken).ConfigureAwait(false);
        if (anchor is null)
        {
            return [];
        }

        var ids = new List<long>();
        ids.AddRange((await ReadSideAsync(connection, anchor.Value.sourceIp, anchor.Value.receivedUtc, eventId,
            newer: false, limit: before, cancellationToken).ConfigureAwait(false)).AsEnumerable().Reverse());
        ids.Add(eventId);
        ids.AddRange(await ReadSideAsync(connection, anchor.Value.sourceIp, anchor.Value.receivedUtc, eventId,
            newer: true, limit: after, cancellationToken).ConfigureAwait(false));

        var result = new List<SyslogEvent>(ids.Count);
        foreach (long id in ids)
        {
            SyslogEvent? evt = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (evt is not null)
            {
                result.Add(evt);
            }
        }

        return result;
    }

    /// <summary>
    /// Deletes events with <c>received_utc</c> strictly before <paramref name="cutoffUtc"/>,
    /// in chunks so no single write transaction holds the lock for long. Returns the total
    /// rows removed. Retention <em>policy</em> (per-stream tiers, archival) is Phase 10;
    /// this is the primitive it drives.
    /// </summary>
    public async Task<long> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        string cutoff = StorageFormat.Timestamp(cutoffUtc);
        long total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            int removed;
            try
            {
                // Stage the chunk's ids, then remove their FTS entries (external content: the
                // 'delete' command with the stored text) and the rows. All set-based, one
                // transaction, so FTS never drifts from the event store.
                await using (SqliteCommand stage = connection.CreateCommand())
                {
                    stage.Transaction = transaction;
                    stage.CommandText = """
                        CREATE TEMP TABLE IF NOT EXISTS _purge_ids (event_id INTEGER PRIMARY KEY);
                        DELETE FROM _purge_ids;
                        INSERT INTO _purge_ids
                            SELECT event_id FROM events WHERE received_utc < $cutoff
                            ORDER BY event_id LIMIT $chunk;
                        """;
                    stage.Parameters.AddWithValue("$cutoff", cutoff);
                    stage.Parameters.AddWithValue("$chunk", _retentionChunk);
                    await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (SqliteCommand count = connection.CreateCommand())
                {
                    count.Transaction = transaction;
                    count.CommandText = "SELECT COUNT(*) FROM _purge_ids;";
                    removed = Convert.ToInt32(
                        await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                if (removed > 0)
                {
                    await using SqliteCommand delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = """
                        INSERT INTO events_fts (events_fts, rowid, search_text)
                            SELECT 'delete', e.event_id, e.search_text
                            FROM events e JOIN _purge_ids p ON p.event_id = e.event_id
                            WHERE e.event_id <= (SELECT last_indexed_event_id FROM fts_state WHERE id = 1);
                        DELETE FROM events WHERE event_id IN (SELECT event_id FROM _purge_ids);
                        """;
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            total += removed;
            if (removed < _retentionChunk)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Rebuilds the full-text index from the event store. A repair / maintenance operation
    /// (not on any hot path); Phase 10/11 schedule it.
    /// </summary>
    public async Task RebuildSearchIndexAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO events_fts (events_fts) VALUES ('rebuild');";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a passive WAL checkpoint, moving committed pages into the main database file
    /// without blocking writers. Called on the maintainer's interval so the WAL stays
    /// small during quiet periods; auto-checkpoint bounds it under load.
    /// </summary>
    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- filtering -----

    private static void AppendFilter(SqliteCommand command, StringBuilder sql, LogQuery query, bool forCount)
    {
        var clauses = new List<string>();

        if (query.FromUtc is { } from)
        {
            clauses.Add("e.received_utc >= $from");
            command.Parameters.AddWithValue("$from", StorageFormat.Timestamp(from));
        }

        if (query.ToUtc is { } to)
        {
            clauses.Add("e.received_utc < $to");
            command.Parameters.AddWithValue("$to", StorageFormat.Timestamp(to));
        }

        if (query.Severities.Count > 0)
        {
            clauses.Add($"e.severity IN ({BindList(command, "sev", query.Severities.Select(s => (object)(int)s))})");
        }

        if (query.DeviceIds.Count > 0)
        {
            clauses.Add($"e.device_id IN ({BindList(command, "dev", query.DeviceIds.Select(d => (object)d))})");
        }

        if (query.StreamIds.Count > 0)
        {
            clauses.Add($"EXISTS (SELECT 1 FROM event_streams es WHERE es.event_id = e.event_id " +
                        $"AND es.stream_id IN ({BindList(command, "str", query.StreamIds.Select(s => (object)s))}))");
        }

        if (!string.IsNullOrWhiteSpace(query.FullText))
        {
            clauses.Add("e.event_id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH $fts)");
            command.Parameters.AddWithValue("$fts", ToFtsPhrase(query.FullText));
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }

        _ = forCount;
    }

    private static string BindList(SqliteCommand command, string prefix, IEnumerable<object> values)
    {
        var names = new List<string>();
        int i = 0;
        foreach (object value in values)
        {
            string name = $"${prefix}{i++}";
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    /// <summary>
    /// Wraps user text as a single quoted FTS5 phrase so query operators cannot be
    /// injected. NUL is stripped (FTS5's query parser treats it as end-of-string, which
    /// would raise "unterminated string").
    /// </summary>
    internal static string ToFtsPhrase(string text) =>
        "\"" + StorageFormat.SanitizeText(text).Replace("\"", "\"\"") + "\"";

    private static string Prefixed(string columns, string alias) =>
        string.Join(", ", columns.Split(',', StringSplitOptions.TrimEntries).Select(c => $"{alias}.{c}"));

    // ----- field loading -----

    private static async Task<Dictionary<long, IReadOnlyList<EventField>>> LoadFieldsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<long> eventIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, IReadOnlyList<EventField>>();
        if (eventIds.Count == 0)
        {
            return map;
        }

        await using SqliteCommand command = connection.CreateCommand();
        string list = BindList(command, "id", eventIds.Select(id => (object)id));
        command.CommandText = $"SELECT event_id, name, value FROM event_fields WHERE event_id IN ({list}) ORDER BY event_id, rowid;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            if (!map.TryGetValue(id, out IReadOnlyList<EventField>? bucket))
            {
                bucket = new List<EventField>();
                map[id] = bucket;
            }

            ((List<EventField>)bucket).Add(new EventField(reader.GetString(1), reader.GetString(2)));
        }

        return map;
    }

    private static SyslogEvent CloneWithFields(SyslogEvent evt, IReadOnlyList<EventField> fields) => new()
    {
        EventId = evt.EventId,
        ReceivedUtc = evt.ReceivedUtc,
        EventUtc = evt.EventUtc,
        SourceIp = evt.SourceIp,
        Hostname = evt.Hostname,
        AppName = evt.AppName,
        ProcId = evt.ProcId,
        MsgId = evt.MsgId,
        Facility = evt.Facility,
        Severity = evt.Severity,
        Protocol = evt.Protocol,
        ListenerId = evt.ListenerId,
        Message = evt.Message,
        RawMessage = evt.RawMessage,
        ParseStatus = evt.ParseStatus,
        OccurrenceCount = evt.OccurrenceCount,
        StructuredDataJson = evt.StructuredDataJson,
        DeviceId = evt.DeviceId,
        Vendor = evt.Vendor,
        Fields = fields,
    };

    private static async Task<(string sourceIp, string receivedUtc)?> ReadAnchorAsync(
        SqliteConnection connection, long eventId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT source_ip, received_utc FROM events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<List<long>> ReadSideAsync(
        SqliteConnection connection,
        string sourceIp,
        string receivedUtc,
        long anchorId,
        bool newer,
        int limit,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        if (limit == 0)
        {
            return ids;
        }

        string comparison = newer
            ? "(e.received_utc > $ts OR (e.received_utc = $ts AND e.event_id > $id))"
            : "(e.received_utc < $ts OR (e.received_utc = $ts AND e.event_id < $id))";
        string order = newer ? "ASC" : "DESC";

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT e.event_id FROM events e WHERE e.source_ip = $ip AND {comparison} " +
            $"ORDER BY e.received_utc {order}, e.event_id {order} LIMIT $limit;";
        command.Parameters.AddWithValue("$ip", sourceIp);
        command.Parameters.AddWithValue("$ts", receivedUtc);
        command.Parameters.AddWithValue("$id", anchorId);
        command.Parameters.AddWithValue("$limit", limit);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private sealed class EventParameters
    {
        // Pre-boxed small integers so the hot bind loop does not allocate a box per row
        // for facility (0-23), severity (0-7), and occurrence_count (usually 1).
        private static readonly object[] FacilityBox = Enumerable.Range(0, 24).Select(i => (object)i).ToArray();
        private static readonly object[] SeverityBox = Enumerable.Range(0, 8).Select(i => (object)i).ToArray();
        private static readonly object[] SmallCountBox = Enumerable.Range(0, 65).Select(i => (object)i).ToArray();

        private readonly SqliteParameter _receivedUtc;
        private readonly SqliteParameter _eventUtc;
        private readonly SqliteParameter _sourceIp;
        private readonly SqliteParameter _hostname;
        private readonly SqliteParameter _appName;
        private readonly SqliteParameter _procId;
        private readonly SqliteParameter _msgId;
        private readonly SqliteParameter _facility;
        private readonly SqliteParameter _severity;
        private readonly SqliteParameter _protocol;
        private readonly SqliteParameter _listenerId;
        private readonly SqliteParameter _message;
        private readonly SqliteParameter _rawMessage;
        private readonly SqliteParameter _searchText;
        private readonly SqliteParameter _parseStatus;
        private readonly SqliteParameter _occurrenceCount;
        private readonly SqliteParameter _structuredData;
        private readonly SqliteParameter _deviceId;
        private readonly SqliteParameter _vendor;

        private EventParameters(SqliteCommand command)
        {
            _receivedUtc = command.Parameters.Add("$received_utc", SqliteType.Text);
            _eventUtc = command.Parameters.Add("$event_utc", SqliteType.Text);
            _sourceIp = command.Parameters.Add("$source_ip", SqliteType.Text);
            _hostname = command.Parameters.Add("$hostname", SqliteType.Text);
            _appName = command.Parameters.Add("$app_name", SqliteType.Text);
            _procId = command.Parameters.Add("$proc_id", SqliteType.Text);
            _msgId = command.Parameters.Add("$msg_id", SqliteType.Text);
            _facility = command.Parameters.Add("$facility", SqliteType.Integer);
            _severity = command.Parameters.Add("$severity", SqliteType.Integer);
            _protocol = command.Parameters.Add("$protocol", SqliteType.Text);
            _listenerId = command.Parameters.Add("$listener_id", SqliteType.Integer);
            _message = command.Parameters.Add("$message", SqliteType.Text);
            _rawMessage = command.Parameters.Add("$raw_message", SqliteType.Blob);
            _searchText = command.Parameters.Add("$search_text", SqliteType.Text);
            _parseStatus = command.Parameters.Add("$parse_status", SqliteType.Text);
            _occurrenceCount = command.Parameters.Add("$occurrence_count", SqliteType.Integer);
            _structuredData = command.Parameters.Add("$structured_data_json", SqliteType.Text);
            _deviceId = command.Parameters.Add("$device_id", SqliteType.Integer);
            _vendor = command.Parameters.Add("$vendor", SqliteType.Text);
        }

        public static EventParameters AddTo(SqliteCommand command) => new(command);

        /// <summary>Binds every parameter and returns the computed <c>search_text</c> for FTS.</summary>
        public string Bind(SyslogEvent evt)
        {
            _receivedUtc.Value = StorageFormat.Timestamp(evt.ReceivedUtc);
            _eventUtc.Value = evt.EventUtc is { } eu ? StorageFormat.Timestamp(eu) : DBNull.Value;
            _sourceIp.Value = StorageFormat.SanitizeText(evt.SourceIp);
            _hostname.Value = Text(evt.Hostname);
            _appName.Value = Text(evt.AppName);
            _procId.Value = Text(evt.ProcId);
            _msgId.Value = Text(evt.MsgId);
            int facility = (int)evt.Facility;
            _facility.Value = (uint)facility < FacilityBox.Length ? FacilityBox[facility] : facility;
            int severity = (int)evt.Severity;
            _severity.Value = (uint)severity < SeverityBox.Length ? SeverityBox[severity] : severity;
            _protocol.Value = StorageFormat.Protocol(evt.Protocol);
            _listenerId.Value = evt.ListenerId > 0 ? evt.ListenerId : DBNull.Value;
            string message = StorageFormat.SanitizeText(evt.Message ?? string.Empty);
            string searchText = message.Length > 0 ? message : StorageFormat.RawText(evt.RawMessage);
            _message.Value = message;
            _rawMessage.Value = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(evt.RawMessage, out ArraySegment<byte> segment)
                && segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array
                : evt.RawMessage.ToArray();
            _searchText.Value = searchText;
            _parseStatus.Value = StorageFormat.ParseStatus(evt.ParseStatus);
            int occurrence = Math.Max(1, evt.OccurrenceCount);
            _occurrenceCount.Value = (uint)occurrence < SmallCountBox.Length ? SmallCountBox[occurrence] : occurrence;
            _structuredData.Value = Text(evt.StructuredDataJson);
            _deviceId.Value = evt.DeviceId is { } d ? d : DBNull.Value;
            _vendor.Value = Text(evt.Vendor);

            return searchText;
        }

        private static object Text(string? value) =>
            value is null ? DBNull.Value : StorageFormat.SanitizeText(value);
    }

    private static class EventReader
    {
        public static SyslogEvent Read(SqliteDataReader reader)
        {
            // Column order matches EventColumns.
            long eventId = reader.GetInt64(0);
            var received = StorageFormat.ParseTimestamp(reader.GetString(1));
            DateTimeOffset? eventUtc = reader.IsDBNull(2) ? null : StorageFormat.ParseTimestamp(reader.GetString(2));
            string sourceIp = reader.GetString(3);
            string? hostname = reader.IsDBNull(4) ? null : reader.GetString(4);
            string? appName = reader.IsDBNull(5) ? null : reader.GetString(5);
            string? procId = reader.IsDBNull(6) ? null : reader.GetString(6);
            string? msgId = reader.IsDBNull(7) ? null : reader.GetString(7);
            var facility = (Facility)reader.GetInt32(8);
            var severity = (Severity)reader.GetInt32(9);
            Protocol protocol = StorageFormat.ParseProtocol(reader.GetString(10));
            long listenerId = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);
            string message = reader.GetString(12);
            byte[] raw = (byte[])reader[13];
            ParseStatus parseStatus = StorageFormat.ParseParseStatus(reader.GetString(14));
            int occurrence = reader.GetInt32(15);
            string? structured = reader.IsDBNull(16) ? null : reader.GetString(16);
            long? deviceId = reader.IsDBNull(17) ? null : reader.GetInt64(17);
            string? vendor = reader.IsDBNull(18) ? null : reader.GetString(18);

            return new SyslogEvent
            {
                EventId = eventId,
                ReceivedUtc = received,
                EventUtc = eventUtc,
                SourceIp = sourceIp,
                Hostname = hostname,
                AppName = appName,
                ProcId = procId,
                MsgId = msgId,
                Facility = facility,
                Severity = severity,
                Protocol = protocol,
                ListenerId = listenerId,
                Message = message,
                RawMessage = raw,
                ParseStatus = parseStatus,
                OccurrenceCount = occurrence,
                StructuredDataJson = structured,
                DeviceId = deviceId,
                Vendor = vendor,
            };
        }
    }
}
