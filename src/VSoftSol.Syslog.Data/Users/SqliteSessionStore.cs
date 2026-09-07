using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Users;

/// <summary>
/// Server-side session records (PHASE_04 build items 5 and the "Session attacks" validation
/// item: fixation, hijacking, "logout not invalidating server-side", concurrent sessions,
/// absolute and idle timeout). A session id is a 256-bit CSPRNG value issued only here.
/// </summary>
public sealed class SqliteSessionStore
{
    private const string Columns =
        "s.session_id, s.user_id, u.username, s.created_utc, s.last_seen_utc, s.absolute_expiry_utc, " +
        "s.source_ip, s.user_agent, s.revoked_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqliteSessionStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Mints a fresh session for <paramref name="userId"/> and returns its id.</summary>
    public async Task<string> CreateAsync(
        long userId,
        DateTimeOffset nowUtc,
        TimeSpan absoluteLifetime,
        string? sourceIp,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        string sessionId = Base64Url(RandomNumberGenerator.GetBytes(32));

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_sessions
              (session_id, user_id, created_utc, last_seen_utc, absolute_expiry_utc, source_ip, user_agent)
            VALUES
              ($id, $userId, $now, $now, $expiry, $ip, $ua);
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        command.Parameters.AddWithValue("$expiry", Iso(nowUtc + absoluteLifetime));
        command.Parameters.AddWithValue("$ip", (object?)sourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$ua", (object?)Truncate(userAgent, 512) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return sessionId;
    }

    public async Task<UserSession?> FindAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM user_sessions s JOIN users u ON u.user_id = s.user_id
            WHERE s.session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Bumps <c>last_seen_utc</c> (idle-timeout sliding window). No-op for a missing/revoked row.</summary>
    public async Task TouchAsync(string sessionId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE user_sessions SET last_seen_utc = $now WHERE session_id = $id AND revoked_utc IS NULL;";
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RevokeAsync(string sessionId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE user_sessions SET revoked_utc = $now WHERE session_id = $id AND revoked_utc IS NULL;",
            cancellationToken, ("$now", Iso(nowUtc)), ("$id", sessionId));

    public Task RevokeAllForUserAsync(long userId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE user_sessions SET revoked_utc = $now WHERE user_id = $id AND revoked_utc IS NULL;",
            cancellationToken, ("$now", Iso(nowUtc)), ("$id", userId));

    public async Task<IReadOnlyList<UserSession>> ListActiveForUserAsync(
        long userId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM user_sessions s JOIN users u ON u.user_id = s.user_id
            WHERE s.user_id = $id AND s.revoked_utc IS NULL AND s.absolute_expiry_utc > $now
            ORDER BY s.last_seen_utc DESC;
            """;
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        var result = new List<UserSession>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    /// <summary>Deletes rows whose absolute expiry has passed. Returns the count removed. For the maintenance job.</summary>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_sessions WHERE absolute_expiry_utc <= $now;";
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UserSession Map(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(0),
        UserId = reader.GetInt64(1),
        Username = reader.GetString(2),
        CreatedUtc = ParseIso(reader.GetString(3)),
        LastSeenUtc = ParseIso(reader.GetString(4)),
        AbsoluteExpiryUtc = ParseIso(reader.GetString(5)),
        SourceIp = reader.IsDBNull(6) ? null : reader.GetString(6),
        UserAgent = reader.IsDBNull(7) ? null : reader.GetString(7),
        RevokedUtc = reader.IsDBNull(8) ? null : ParseIso(reader.GetString(8)),
    };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
