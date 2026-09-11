using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Security;

public sealed record ApiKeyRecord(
    long Id, string Label, string Purpose, string? AllowedSourceIp,
    DateTimeOffset CreatedUtc, string? CreatedBy, DateTimeOffset? RevokedUtc)
{
    public bool IsActive => RevokedUtc is null;
}

/// <summary>
/// API keys for machine-to-machine intake (PHASE_11 item 3: the Windows Event Log
/// endpoint). Only a SHA-256 hash is ever stored — the plaintext key is generated,
/// returned once to the caller, and never persisted or retrievable again (the same
/// convention as MFA recovery codes: a high-entropy random value needs no slow KDF).
/// </summary>
public sealed class SqliteApiKeyStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteApiKeyStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public static string GenerateKey() => "vsk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<long> CreateAsync(
        string label, string plaintextKey, string purpose, string? allowedSourceIp, string? createdBy,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO api_keys (label, key_hash, purpose, allowed_source_ip, created_utc, created_by)
            VALUES ($label, $hash, $purpose, $ip, $created, $by);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$label", label.Trim());
        command.Parameters.AddWithValue("$hash", Hash(plaintextKey));
        command.Parameters.AddWithValue("$purpose", purpose);
        command.Parameters.AddWithValue("$ip", (object?)allowedSourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Iso(nowUtc));
        command.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    public async Task RevokeAsync(long id, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE api_keys SET revoked_utc = $now WHERE id = $id AND revoked_utc IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(string purpose, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, label, purpose, allowed_source_ip, created_utc, created_by, revoked_utc
              FROM api_keys WHERE purpose = $purpose ORDER BY created_utc DESC;
            """;
        command.Parameters.AddWithValue("$purpose", purpose);
        var result = new List<ApiKeyRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    /// <summary>Fail-closed: an unknown/hash-mismatched/revoked key, or one scoped to a
    /// different source IP than <paramref name="sourceIp"/>, is rejected.</summary>
    public async Task<bool> IsValidAsync(string purpose, string plaintextKey, string sourceIp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(plaintextKey))
        {
            return false;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT allowed_source_ip FROM api_keys
             WHERE purpose = $purpose AND key_hash = $hash AND revoked_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$purpose", purpose);
        command.Parameters.AddWithValue("$hash", Hash(plaintextKey));
        object? allowedIp = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (allowedIp is null)
        {
            return false; // no active key with this hash
        }

        return allowedIp is DBNull || string.Equals((string)allowedIp, sourceIp, StringComparison.Ordinal);
    }

    private static ApiKeyRecord Map(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        ParseIso(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : ParseIso(reader.GetString(6)));

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
