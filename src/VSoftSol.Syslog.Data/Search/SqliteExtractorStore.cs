using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>An operator-authored extractor saved from the Phase 5 pattern tester.</summary>
public sealed record UserExtractor(
    long Id, string Name, string Kind, string Pattern, string? Sample, bool Enabled,
    string? CreatedBy, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

/// <summary>
/// Persists patterns built and verified in the pattern tester (PHASE_05 item 8). These are
/// applied by the ingest extractor pipeline once the Phase 6 configuration surface wires
/// them in; for now this is storage + retrieval.
/// </summary>
public sealed class SqliteExtractorStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<UserExtractor>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT extractor_id, name, kind, pattern, sample, enabled, created_by, created_utc, updated_utc " +
            "FROM user_extractors ORDER BY name COLLATE NOCASE;";

        var result = new List<UserExtractor>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new UserExtractor(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                StorageFormat.ParseTimestamp(reader.GetString(7)), StorageFormat.ParseTimestamp(reader.GetString(8))));
        }

        return result;
    }

    public async Task<long> SaveAsync(
        string name, string kind, string pattern, string? sample, string? createdBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (kind is not ("grok" or "regex"))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "kind must be 'grok' or 'regex'.");
        }

        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO user_extractors (name, kind, pattern, sample, created_by, created_utc, updated_utc) " +
            "VALUES ($name, $kind, $pat, $sample, $by, $now, $now) " +
            "ON CONFLICT (name) DO UPDATE SET kind = $kind, pattern = $pat, sample = $sample, updated_utc = $now; " +
            "SELECT extractor_id FROM user_extractors WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$pat", pattern);
        command.Parameters.AddWithValue("$sample", (object?)sample ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
