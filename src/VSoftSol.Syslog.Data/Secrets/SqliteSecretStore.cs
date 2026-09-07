using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Secrets;

/// <summary>
/// Named secret storage for SMTP passwords, webhook tokens, and ODBC strings (PHASE_04
/// build item 8; consumed from Phase 7). Values are protected by <see cref="ISecretProtector"/>
/// before they touch the database. The plaintext is never logged, never returned by
/// <see cref="ListNamesAsync"/>, and never included in a config-bundle export.
/// </summary>
public sealed class SqliteSecretStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;

    public SqliteSecretStore(SqliteConnectionFactory factory, ISecretProtector protector, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task SetAsync(string name, string plaintext, string? updatedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] ciphertext = _protector.Protect(Encoding.UTF8.GetBytes(plaintext));

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO secrets (name, protected_value, updated_utc, updated_by)
            VALUES ($name, $value, $updated, $by)
            ON CONFLICT(name) DO UPDATE SET
                protected_value = excluded.protected_value,
                updated_utc = excluded.updated_utc,
                updated_by = excluded.updated_by;
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$value", ciphertext);
        command.Parameters.AddWithValue("$updated", Iso(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$by", (object?)updatedBy ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT protected_value FROM secrets WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name.Trim());

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not byte[] ciphertext)
        {
            return null;
        }

        return Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM secrets WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>The configured secret names only — never the values.</summary>
    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM secrets ORDER BY name;";
        var names = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
