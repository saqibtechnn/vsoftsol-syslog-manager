using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Security;

/// <summary>A pending second-factor challenge, issued after a correct password and before
/// a session exists.</summary>
public sealed record MfaLoginChallenge(string Token, long UserId, int AttemptsMade, DateTimeOffset ExpiresUtc)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresUtc;
}

/// <summary>
/// Short-lived, single-use MFA login challenges (v1.1, B11-3). A dumb CRUD store — attempt
/// limits and expiry policy are enforced by the caller (<see cref="VSoftSol.Syslog.Web.Security.AuthSessionService"/>),
/// this type just persists and increments what it is told to.
/// </summary>
public sealed class SqliteMfaLoginChallengeStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMfaLoginChallengeStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Creates a new challenge with a cryptographically random, unguessable token.</summary>
    public async Task<string> CreateAsync(long userId, DateTimeOffset nowUtc, TimeSpan validFor, CancellationToken cancellationToken)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mfa_login_challenges (challenge_token, user_id, attempts_made, created_utc, expires_utc)
            VALUES ($token, $userId, 0, $created, $expires);
            """;
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$created", Iso(nowUtc));
        command.Parameters.AddWithValue("$expires", Iso(nowUtc + validFor));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<MfaLoginChallenge?> FindAsync(string token, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT challenge_token, user_id, attempts_made, expires_utc FROM mfa_login_challenges WHERE challenge_token = $token;";
        command.Parameters.AddWithValue("$token", token);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MfaLoginChallenge(
            reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
    }

    /// <summary>Atomically increments the attempt counter and returns the new count.</summary>
    public async Task<int> IncrementAttemptsAsync(string token, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mfa_login_challenges SET attempts_made = attempts_made + 1 WHERE challenge_token = $token
            RETURNING attempts_made;
            """;
        command.Parameters.AddWithValue("$token", token);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task DeleteAsync(string token, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mfa_login_challenges WHERE challenge_token = $token;";
        command.Parameters.AddWithValue("$token", token);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
