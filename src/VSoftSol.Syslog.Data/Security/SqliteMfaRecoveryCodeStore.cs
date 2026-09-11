using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Security;

/// <summary>
/// One-time MFA recovery codes (PHASE_11 item 8). Only hashes are ever stored — the codes
/// themselves are shown to the user once, at enrollment or regeneration, and never again.
/// </summary>
public sealed class SqliteMfaRecoveryCodeStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMfaRecoveryCodeStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Enrollment or regeneration: discards any previous set and stores the new
    /// hashes, all in one transaction.</summary>
    public async Task ReplaceAllAsync(long userId, IReadOnlyList<string> codeHashes, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM mfa_recovery_codes WHERE user_id = $id;";
                delete.Parameters.AddWithValue("$id", userId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO mfa_recovery_codes (user_id, code_hash, created_utc) VALUES ($id, $hash, $created);";
            insert.Parameters.AddWithValue("$id", userId);
            SqliteParameter hashParam = insert.Parameters.Add("$hash", SqliteType.Text);
            insert.Parameters.AddWithValue("$created", Iso(nowUtc));
            foreach (string hash in codeHashes)
            {
                hashParam.Value = hash;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Atomically marks one unused, matching code as used. Returns false — never
    /// throws — for a wrong code or a code already consumed (reuse rejection).</summary>
    public async Task<bool> TryConsumeAsync(long userId, string codeHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mfa_recovery_codes SET used_utc = $now
             WHERE id = (
                 SELECT id FROM mfa_recovery_codes
                  WHERE user_id = $id AND code_hash = $hash AND used_utc IS NULL
                  LIMIT 1
             );
            """;
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$hash", codeHash);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> CountUnusedAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mfa_recovery_codes WHERE user_id = $id AND used_utc IS NULL;";
        command.Parameters.AddWithValue("$id", userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
