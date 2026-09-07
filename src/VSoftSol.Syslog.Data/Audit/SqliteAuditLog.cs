using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Audit;

/// <summary>
/// Append-only audit log. The <c>audit_log</c> triggers (migration 001) block UPDATE and
/// DELETE through any SQL path; this class additionally maintains a SHA-256 hash chain
/// (<c>entry_hash = SHA-256(prev_hash || canonical(row))</c>) so out-of-band tampering —
/// a doctored database file, a restored bad backup — is detectable via
/// <see cref="VerifyChainAsync"/>. There is deliberately no update or delete method here.
/// </summary>
public sealed class SqliteAuditLog
{
    private const string Columns =
        "audit_id, occurred_utc, actor, action, entity_type, entity_id, source_ip, before_json, after_json, detail";

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteAuditLog(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);

        string occurredUtc = _time.GetUtcNow().ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? prevHash;
            await using (SqliteCommand tail = connection.CreateCommand())
            {
                tail.Transaction = transaction;
                tail.CommandText = "SELECT entry_hash FROM audit_log ORDER BY audit_id DESC LIMIT 1;";
                object? value = await tail.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                prevHash = value is string s ? s : null;
            }

            string entryHash = ComputeHash(prevHash, occurredUtc, entry);

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO audit_log
                  (occurred_utc, actor, action, entity_type, entity_id, source_ip, before_json, after_json, detail,
                   prev_hash, entry_hash)
                VALUES
                  ($occurred, $actor, $action, $entityType, $entityId, $sourceIp, $before, $after, $detail,
                   $prev, $hash);
                """;
            insert.Parameters.AddWithValue("$occurred", occurredUtc);
            insert.Parameters.AddWithValue("$actor", (object?)entry.Actor ?? DBNull.Value);
            insert.Parameters.AddWithValue("$action", entry.Action);
            insert.Parameters.AddWithValue("$entityType", (object?)entry.EntityType ?? DBNull.Value);
            insert.Parameters.AddWithValue("$entityId", (object?)entry.EntityId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sourceIp", (object?)entry.SourceIp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$before", (object?)entry.BeforeJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$after", (object?)entry.AfterJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$detail", (object?)entry.Detail ?? DBNull.Value);
            insert.Parameters.AddWithValue("$prev", (object?)prevHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$hash", entryHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        var sql = new StringBuilder($"SELECT {Columns} FROM audit_log");
        var clauses = new List<string>();
        if (query.FromUtc is { } from)
        {
            clauses.Add("occurred_utc >= $from");
            command.Parameters.AddWithValue("$from", Iso(from));
        }

        if (query.ToUtc is { } to)
        {
            clauses.Add("occurred_utc < $to");
            command.Parameters.AddWithValue("$to", Iso(to));
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            clauses.Add("actor = $actor COLLATE NOCASE");
            command.Parameters.AddWithValue("$actor", query.Actor);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            clauses.Add("action = $action");
            command.Parameters.AddWithValue("$action", query.Action);
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }

        sql.Append(" ORDER BY audit_id DESC LIMIT $limit OFFSET $offset;");
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5_000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        command.CommandText = sql.ToString();

        var result = new List<AuditRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AuditRecord(
                reader.GetInt64(0),
                ParseIso(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return result;
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_log;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Walks the whole chain oldest-first and reports the first link that does not verify.</summary>
    public async Task<AuditChainVerification> VerifyChainAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT audit_id, occurred_utc, actor, action, entity_type, entity_id, source_ip, before_json, after_json, detail, " +
            "prev_hash, entry_hash FROM audit_log ORDER BY audit_id ASC;";

        string? expectedPrev = null;
        long checked_ = 0;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long auditId = reader.GetInt64(0);
            var entry = new AuditEntry(
                reader.GetString(3),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
            string occurredUtc = reader.GetString(1);
            string? storedPrev = reader.IsDBNull(10) ? null : reader.GetString(10);
            string? storedHash = reader.IsDBNull(11) ? null : reader.GetString(11);

            string recomputed = ComputeHash(storedPrev, occurredUtc, entry);
            if (!string.Equals(storedPrev, expectedPrev, StringComparison.Ordinal)
                || !string.Equals(storedHash, recomputed, StringComparison.Ordinal))
            {
                return new AuditChainVerification(false, checked_, auditId);
            }

            expectedPrev = storedHash;
            checked_++;
        }

        return new AuditChainVerification(true, checked_, null);
    }

    private static string ComputeHash(string? prevHash, string occurredUtc, AuditEntry e)
    {
        // Canonical form: newline-joined fields, each prefixed so a value cannot masquerade
        // as the next field. NUL-free by construction (all inputs are our own strings/JSON).
        var sb = new StringBuilder(256);
        sb.Append("prev:").Append(prevHash ?? string.Empty).Append('\n');
        sb.Append("occurred:").Append(occurredUtc).Append('\n');
        sb.Append("actor:").Append(e.Actor ?? string.Empty).Append('\n');
        sb.Append("action:").Append(e.Action).Append('\n');
        sb.Append("entityType:").Append(e.EntityType ?? string.Empty).Append('\n');
        sb.Append("entityId:").Append(e.EntityId ?? string.Empty).Append('\n');
        sb.Append("sourceIp:").Append(e.SourceIp ?? string.Empty).Append('\n');
        sb.Append("before:").Append(e.BeforeJson ?? string.Empty).Append('\n');
        sb.Append("after:").Append(e.AfterJson ?? string.Empty).Append('\n');
        sb.Append("detail:").Append(e.Detail ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
