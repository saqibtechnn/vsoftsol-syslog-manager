using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Retention.Compression;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>
/// The tiering engine (PHASE_10 build items 1-5). Every operation processes one bounded
/// batch and returns how many rows it touched — the caller (a <c>BackgroundService</c>)
/// loops and yields between batches, so the writer lock is never held longer than one
/// batch's transaction (CLAUDE.md Constraint 3: never block the writer for long).
///
/// <para>
/// An event's <b>primary stream</b> — the most specific (lowest stream_id) non-catch-all
/// stream it belongs to, falling back to the catch-all or the global default — owns its
/// retention policy. This is a deliberate simplification over "the most conservative
/// policy across every stream the event belongs to": it is unambiguous, cheap to express
/// as one indexed correlated subquery, and matches how an administrator already thinks
/// about "this event's stream" for every other screen in the product.
/// </para>
/// </summary>
public sealed class SqliteRetentionEngine
{
    private const string EventPolicyCte = """
        WITH event_policy AS (
            SELECT es.event_id,
                   s.stream_id,
                   s.name AS stream_name,
                   ROW_NUMBER() OVER (PARTITION BY es.event_id ORDER BY s.is_catch_all ASC, s.stream_id ASC) AS rn,
                   COALESCE(rp.hot_days, $defaultHot) AS hot_days,
                   COALESCE(rp.warm_days, $defaultWarm) AS warm_days,
                   rp.archive_path AS archive_path,
                   COALESCE(rp.compression_level, $defaultLevel) AS compression_level
            FROM event_streams es
            JOIN streams s ON s.stream_id = es.stream_id
            LEFT JOIN retention_policies rp ON rp.stream_id = s.stream_id
        )
        """;

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteRetentionPolicyStore _policies;
    private readonly SqliteArchiveStore _archives;
    private readonly SqliteRestoreStore _restores;
    private readonly TimeProvider _time;
    private readonly CompressorFactory _compressor;

    /// <summary>Hard ceiling on a single archive's decompressed size during restore, independent
    /// of what the file claims — the decompression-bomb defence (PHASE_10 security section).</summary>
    public const long MaxRestoreDecompressedBytes = 2_000_000_000L;

    public SqliteRetentionEngine(
        SqliteConnectionFactory factory,
        SqliteRetentionPolicyStore policies,
        SqliteArchiveStore archives,
        SqliteRestoreStore restores,
        CompressorFactory? compressor = null,
        TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _restores = restores ?? throw new ArgumentNullException(nameof(restores));
        _compressor = compressor ?? new CompressorFactory();
        _time = timeProvider ?? TimeProvider.System;
    }

    // ------------------------------------------------------------- warm

    /// <summary>Compresses one batch of Hot rows past their stream's Warm threshold in place.</summary>
    public async Task<int> TierToWarmBatchAsync(CancellationToken cancellationToken)
    {
        RetentionSettings settings = await _policies.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        long checkpoint = await GetCheckpointAsync("warm", cancellationToken).ConfigureAwait(false);
        long nowEpoch = _time.GetUtcNow().ToUnixTimeSeconds();

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var candidates = new List<(long Id, string Message, byte[] Raw)>();
            await using (SqliteCommand select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = EventPolicyCte + """
                    SELECT e.event_id, e.message, e.raw_message
                    FROM events e
                    LEFT JOIN event_policy ep ON ep.event_id = e.event_id AND ep.rn = 1
                    WHERE e.event_id > $checkpoint AND e.tier = 'hot' AND e.restore_id IS NULL
                      AND CAST(strftime('%s', e.received_utc) AS INTEGER) <= $now - 86400 * COALESCE(ep.hot_days, $defaultHot)
                    ORDER BY e.event_id
                    LIMIT $batch;
                    """;
                BindDefaults(select, settings);
                select.Parameters.AddWithValue("$checkpoint", checkpoint);
                select.Parameters.AddWithValue("$now", nowEpoch);
                select.Parameters.AddWithValue("$batch", settings.BatchSize);

                await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add((reader.GetInt64(0), reader.GetString(1), (byte[])reader[2]));
                }
            }

            if (candidates.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            foreach ((long id, string message, byte[] raw) in candidates)
            {
                byte[] compressedMessage = _compressor.Compress(System.Text.Encoding.UTF8.GetBytes(message), settings.CompressionLevel);
                byte[] compressedRaw = _compressor.Compress(raw, settings.CompressionLevel);

                await using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE events SET tier = 'warm', message = $msg, raw_message = $raw WHERE event_id = $id;";
                update.Parameters.AddWithValue("$msg", compressedMessage);
                update.Parameters.AddWithValue("$raw", compressedRaw);
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SetCheckpointAsync(connection, transaction, "warm", candidates[^1].Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return candidates.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------- cold

    /// <summary>Exports one batch of Warm rows past their stream's Cold threshold into
    /// tamper-evidenced archive file(s) — one per distinct primary stream in the batch —
    /// then removes them from the live database. The archive file is written and hashed
    /// <em>before</em> the database transaction that deletes the rows, so a crash between
    /// the two leaves the events intact (re-selected and re-exported, idempotently,
    /// on the next tick) rather than lost.</summary>
    public async Task<int> ExportColdBatchAsync(CancellationToken cancellationToken)
    {
        RetentionSettings settings = await _policies.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        long checkpoint = await GetCheckpointAsync("cold", cancellationToken).ConfigureAwait(false);
        long nowEpoch = _time.GetUtcNow().ToUnixTimeSeconds();

        List<ColdCandidate> candidates = await SelectColdCandidatesAsync(checkpoint, nowEpoch, settings, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return 0;
        }

        foreach (IGrouping<(long? StreamId, string StreamName, string? ArchivePath, int Level), ColdCandidate> group in
            candidates.GroupBy(c => (c.StreamId, c.StreamName, c.ArchivePath, c.CompressionLevel)))
        {
            await ExportGroupAsync(group.Key, [.. group.Select(c => c.EventId)], settings, cancellationToken).ConfigureAwait(false);
        }

        long maxId = candidates.Max(c => c.EventId);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetCheckpointAsync(connection, transaction, "cold", maxId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return candidates.Count;
    }

    private sealed record ColdCandidate(long EventId, long? StreamId, string StreamName, string? ArchivePath, int CompressionLevel);

    private async Task<List<ColdCandidate>> SelectColdCandidatesAsync(
        long checkpoint, long nowEpoch, RetentionSettings settings, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = EventPolicyCte + """
            SELECT e.event_id, ep.stream_id, COALESCE(ep.stream_name, 'unstreamed'), ep.archive_path,
                   COALESCE(ep.compression_level, $defaultLevel)
            FROM events e
            LEFT JOIN event_policy ep ON ep.event_id = e.event_id AND ep.rn = 1
            WHERE e.event_id > $checkpoint AND e.tier = 'warm' AND e.restore_id IS NULL
              AND CAST(strftime('%s', e.received_utc) AS INTEGER) <= $now - 86400 * (COALESCE(ep.hot_days, $defaultHot) + COALESCE(ep.warm_days, $defaultWarm))
            ORDER BY e.event_id
            LIMIT $batch;
            """;
        BindDefaults(command, settings);
        command.Parameters.AddWithValue("$checkpoint", checkpoint);
        command.Parameters.AddWithValue("$now", nowEpoch);
        command.Parameters.AddWithValue("$batch", settings.BatchSize);

        var result = new List<ColdCandidate>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ColdCandidate(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4)));
        }

        return result;
    }

    private async Task ExportGroupAsync(
        (long? StreamId, string StreamName, string? ArchivePath, int Level) key,
        IReadOnlyList<long> eventIds,
        RetentionSettings settings,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var idParams = new List<string>();
        await using SqliteCommand select = connection.CreateCommand();
        int i = 0;
        foreach (long id in eventIds)
        {
            string name = $"$e{i++}";
            select.Parameters.AddWithValue(name, id);
            idParams.Add(name);
        }

        select.CommandText = $"SELECT {EventRowMapper.Prefixed("e")} FROM events e WHERE e.event_id IN ({string.Join(", ", idParams)}) ORDER BY e.event_id;";

        var events = new List<SyslogEvent>();
        await using (SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(EventRowMapper.Read(reader));
            }
        }

        Dictionary<long, IReadOnlyList<EventField>> fieldMap =
            await EventRowMapper.LoadFieldsAsync(connection, eventIds, cancellationToken).ConfigureAwait(false);
        Dictionary<long, List<long>> streamMap = await LoadEventStreamsAsync(connection, eventIds, cancellationToken).ConfigureAwait(false);

        DateTimeOffset periodStart = events.Min(e => e.ReceivedUtc);
        DateTimeOffset periodEnd = events.Max(e => e.ReceivedUtc);

        var rows = events.Select(e => new ArchiveFile.ArchiveEventRow
        {
            EventId = e.EventId,
            ReceivedUtc = StorageFormat.Timestamp(e.ReceivedUtc),
            EventUtc = e.EventUtc is { } eu ? StorageFormat.Timestamp(eu) : null,
            SourceIp = e.SourceIp,
            Hostname = e.Hostname,
            AppName = e.AppName,
            ProcId = e.ProcId,
            MsgId = e.MsgId,
            Facility = (int)e.Facility,
            Severity = (int)e.Severity,
            Protocol = e.Protocol.ToString(),
            ListenerId = e.ListenerId,
            Message = e.Message,
            RawMessageBase64 = Convert.ToBase64String(e.RawMessage.Span),
            ParseStatus = e.ParseStatus.ToString(),
            OccurrenceCount = e.OccurrenceCount,
            StructuredDataJson = e.StructuredDataJson,
            DeviceId = e.DeviceId,
            Vendor = e.Vendor,
            Fields = [.. fieldMap.GetValueOrDefault(e.EventId, []).Select(f => new ArchiveFile.ArchiveFieldRow(f.Name, f.Value))],
            StreamIds = streamMap.GetValueOrDefault(e.EventId, []),
        }).ToList();

        var header = new ArchiveFile.ArchiveHeader
        {
            Product = BrandingInfo.ProductName,
            Version = typeof(ArchiveFile).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            StreamId = key.StreamId,
            StreamName = key.StreamName,
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            EventCount = rows.Count,
            ExportedUtc = _time.GetUtcNow(),
        };

        byte[] content = ArchiveFile.Build(header, rows, _compressor, key.Level);

        string root = ResolveArchiveRoot(key.ArchivePath, settings.ArchiveRoot);
        string fileName = ArchiveNaming.BuildFileName(key.StreamName, periodStart, periodEnd);
        string fullPath = Path.GetFullPath(Path.Combine(root, fileName));
        string rootFull = Path.GetFullPath(root);

        (string sha256, long byteSize) = await ArchiveFileStore.WriteAtomicAsync(rootFull, fullPath, content, cancellationToken).ConfigureAwait(false);

        long archiveId = await _archives.CreateOrReplaceAsync(
            new ArchiveRecord
            {
                StreamId = key.StreamId,
                StreamName = key.StreamName,
                FilePath = fullPath,
                PeriodStartUtc = periodStart,
                PeriodEndUtc = periodEnd,
                EventCount = rows.Count,
                ByteSize = byteSize,
                Sha256 = sha256,
            },
            cancellationToken).ConfigureAwait(false);

        await DeleteEventsAsync(eventIds, cancellationToken).ConfigureAwait(false);
        _ = archiveId;
    }

    // ------------------------------------------------------------- restore

    /// <summary>Restores one archive's events into the live database, tagged with a new
    /// <see cref="RestoreRecord"/> so they auto-expire. The hash is verified <em>before</em>
    /// any decompression or parsing — a tampered file is refused outright, not partially
    /// trusted (SECURITY_STANDARDS.md "Malicious archive import").</summary>
    public async Task<RestoreRecord> RestoreArchiveAsync(
        long archiveId, string requestedBy, TimeSpan expiresAfter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

        ArchiveRecord archive = await _archives.GetAsync(archiveId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Archive {archiveId} does not exist.");

        if (archive.Status == ArchiveStatus.Deleted)
        {
            throw new InvalidOperationException($"Archive {archiveId} has been deleted and cannot be restored.");
        }

        RetentionSettings settings = await _policies.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        string root = ResolveArchiveRoot(null, settings.ArchiveRoot);
        string rootFull = Path.GetFullPath(root);

        byte[] content = await ArchiveFileStore.ReadAllBytesAsync(rootFull, archive.FilePath, cancellationToken).ConfigureAwait(false);

        string actualHash = ArchiveFileStore.ComputeSha256Hex(content);
        if (!string.Equals(actualHash, archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await _archives.MarkTamperedAsync(archiveId, cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException(
                $"Archive {archiveId} failed hash verification (expected {archive.Sha256}, got {actualHash}) — refusing to restore.");
        }

        ArchiveFile.ParsedArchive parsed = ArchiveFile.Parse(content, _compressor, MaxRestoreDecompressedBytes);

        DateTimeOffset now = _time.GetUtcNow();
        long restoreId = await _restores.CreateAsync(archiveId, requestedBy, now + expiresAfter, cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        HashSet<long> liveStreamIds = await LoadLiveStreamIdsAsync(connection, cancellationToken).ConfigureAwait(false);

        // The write lock is released (end of this block) BEFORE SetEventCountAsync below,
        // which acquires it again itself — the non-reentrant SemaphoreSlim would deadlock
        // if the lock were still held at that call (found live via a hung integration run).
        {
            await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (ArchiveFile.ArchiveEventRow row in parsed.Rows)
                {
                    await InsertRestoredEventAsync(connection, transaction, row, restoreId, liveStreamIds, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        await _restores.SetEventCountAsync(restoreId, parsed.Rows.Count, cancellationToken).ConfigureAwait(false);
        return (await _restores.GetAsync(restoreId, cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task InsertRestoredEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, ArchiveFile.ArchiveEventRow row,
        long restoreId, HashSet<long> liveStreamIds, CancellationToken cancellationToken)
    {
        byte[] raw = Convert.FromBase64String(row.RawMessageBase64);

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO events
                    (event_id, received_utc, event_utc, source_ip, hostname, app_name, proc_id, msg_id,
                     facility, severity, protocol, listener_id, message, raw_message, search_text, parse_status,
                     occurrence_count, structured_data_json, device_id, vendor, tier, restore_id)
                VALUES
                    ($id, $received, $eventUtc, $sip, $host, $app, $proc, $msgid, $fac, $sev, $proto, $listener,
                     $msg, $raw, $search, $status, $occ, $sd, $device, $vendor, 'hot', $restore)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$id", row.EventId);
            insert.Parameters.AddWithValue("$received", row.ReceivedUtc);
            insert.Parameters.AddWithValue("$eventUtc", (object?)row.EventUtc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sip", row.SourceIp);
            insert.Parameters.AddWithValue("$host", (object?)row.Hostname ?? DBNull.Value);
            insert.Parameters.AddWithValue("$app", (object?)row.AppName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$proc", (object?)row.ProcId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$msgid", (object?)row.MsgId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$fac", row.Facility);
            insert.Parameters.AddWithValue("$sev", row.Severity);
            insert.Parameters.AddWithValue("$proto", row.Protocol.ToLowerInvariant());
            insert.Parameters.AddWithValue("$listener", row.ListenerId == 0 ? DBNull.Value : row.ListenerId);
            insert.Parameters.AddWithValue("$msg", row.Message);
            insert.Parameters.AddWithValue("$raw", raw);
            insert.Parameters.AddWithValue("$search", string.IsNullOrEmpty(row.Message) ? StorageFormat.RawText(raw) : row.Message);
            insert.Parameters.AddWithValue("$status", row.ParseStatus.ToLowerInvariant());
            insert.Parameters.AddWithValue("$occ", row.OccurrenceCount);
            insert.Parameters.AddWithValue("$sd", (object?)row.StructuredDataJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$device", (object?)row.DeviceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$vendor", (object?)row.Vendor ?? DBNull.Value);
            insert.Parameters.AddWithValue("$restore", restoreId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ArchiveFile.ArchiveFieldRow field in row.Fields)
        {
            await using SqliteCommand insertField = connection.CreateCommand();
            insertField.Transaction = transaction;
            insertField.CommandText = "INSERT INTO event_fields (event_id, name, value) VALUES ($id, $name, $value);";
            insertField.Parameters.AddWithValue("$id", row.EventId);
            insertField.Parameters.AddWithValue("$name", field.Name);
            insertField.Parameters.AddWithValue("$value", field.Value);
            await insertField.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (long streamId in row.StreamIds.Where(liveStreamIds.Contains))
        {
            await using SqliteCommand insertStream = connection.CreateCommand();
            insertStream.Transaction = transaction;
            insertStream.CommandText = "INSERT OR IGNORE INTO event_streams (event_id, stream_id) VALUES ($id, $sid);";
            insertStream.Parameters.AddWithValue("$id", row.EventId);
            insertStream.Parameters.AddWithValue("$sid", streamId);
            await insertStream.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = "INSERT INTO events_fts (rowid, search_text) VALUES ($id, $search);";
        fts.Parameters.AddWithValue("$id", row.EventId);
        fts.Parameters.AddWithValue("$search", string.IsNullOrEmpty(row.Message) ? StorageFormat.RawText(raw) : row.Message);
        await fts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every event belonging to one expired restore. Auditable by design —
    /// the caller records the audit entry (this class has no audit dependency).</summary>
    public async Task<int> ExpireRestoresBatchAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<RestoreRecord> expired = await _restores.ListExpiredAsync(limit, cancellationToken).ConfigureAwait(false);
        int total = 0;
        foreach (RestoreRecord restore in expired)
        {
            // The write lock is released (end of this block) BEFORE MarkExpiredAsync below,
            // which acquires it again itself — see the matching note in RestoreArchiveAsync.
            await using (SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            await using (IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false))
            {
                await using SqliteTransaction transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using (SqliteCommand ftsDelete = connection.CreateCommand())
                    {
                        ftsDelete.Transaction = transaction;
                        ftsDelete.CommandText = """
                            INSERT INTO events_fts (events_fts, rowid, search_text)
                                SELECT 'delete', e.event_id, e.search_text FROM events e WHERE e.restore_id = $rid;
                            DELETE FROM events WHERE restore_id = $rid;
                            """;
                        ftsDelete.Parameters.AddWithValue("$rid", restore.RestoreId);
                        total += await ftsDelete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            await _restores.MarkExpiredAsync(restore.RestoreId, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    /// <summary>Deletes archive files (and marks their rows) past the Delete horizon.</summary>
    public async Task<int> PurgeExpiredArchivesBatchAsync(int limit, CancellationToken cancellationToken)
    {
        RetentionSettings settings = await _policies.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ArchiveRecord> due = await _archives.ListForPurgeAsync(settings.DefaultColdDays, limit, cancellationToken).ConfigureAwait(false);

        foreach (ArchiveRecord archive in due)
        {
            ArchiveFileStore.Delete(archive.FilePath);
            await _archives.MarkDeletedAsync(archive.ArchiveId, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    // ------------------------------------------------------------- helpers

    private async Task DeleteEventsAsync(IReadOnlyList<long> eventIds, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            var idParams = new List<string>();
            int i = 0;
            foreach (long id in eventIds)
            {
                string name = $"$e{i++}";
                command.Parameters.AddWithValue(name, id);
                idParams.Add(name);
            }

            string idList = string.Join(", ", idParams);
            command.CommandText = $"""
                INSERT INTO events_fts (events_fts, rowid, search_text)
                    SELECT 'delete', e.event_id, e.search_text FROM events e
                    WHERE e.event_id IN ({idList}) AND e.event_id <= (SELECT last_indexed_event_id FROM fts_state WHERE id = 1);
                DELETE FROM events WHERE event_id IN ({idList});
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Dictionary<long, List<long>>> LoadEventStreamsAsync(
        SqliteConnection connection, IReadOnlyList<long> eventIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, List<long>>();
        if (eventIds.Count == 0)
        {
            return map;
        }

        await using SqliteCommand command = connection.CreateCommand();
        var names = new List<string>();
        int i = 0;
        foreach (long id in eventIds)
        {
            string name = $"$e{i++}";
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }

        command.CommandText = $"SELECT event_id, stream_id FROM event_streams WHERE event_id IN ({string.Join(", ", names)});";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long eventId = reader.GetInt64(0);
            if (!map.TryGetValue(eventId, out List<long>? list))
            {
                list = [];
                map[eventId] = list;
            }

            list.Add(reader.GetInt64(1));
        }

        return map;
    }

    private static async Task<HashSet<long>> LoadLiveStreamIdsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT stream_id FROM streams;";
        var result = new HashSet<long>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private string ResolveArchiveRoot(string? policyOverride, string globalRoot)
    {
        if (!string.IsNullOrWhiteSpace(policyOverride))
        {
            return policyOverride;
        }

        if (!string.IsNullOrWhiteSpace(globalRoot))
        {
            return globalRoot;
        }

        string dataDir = Path.GetDirectoryName(_factory.DatabasePath) ?? ".";
        return Path.Combine(dataDir, "archives");
    }

    private async Task<long> GetCheckpointAsync(string phase, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT last_event_id FROM tiering_checkpoints WHERE phase = $phase;";
        command.Parameters.AddWithValue("$phase", phase);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task SetCheckpointAsync(
        SqliteConnection connection, SqliteTransaction transaction, string phase, long lastEventId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE tiering_checkpoints SET last_event_id = $id, updated_utc = $now WHERE phase = $phase;";
        command.Parameters.AddWithValue("$id", lastEventId);
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$phase", phase);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindDefaults(SqliteCommand command, RetentionSettings settings)
    {
        command.Parameters.AddWithValue("$defaultHot", settings.DefaultHotDays);
        command.Parameters.AddWithValue("$defaultWarm", settings.DefaultWarmDays);
        command.Parameters.AddWithValue("$defaultLevel", settings.CompressionLevel);
    }
}
