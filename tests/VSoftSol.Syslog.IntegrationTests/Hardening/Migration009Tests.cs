using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

/// <summary>Migration 009 — the Phase 11 MFA / API key / bundle-trust schema.</summary>
public sealed class Migration009Tests
{
    [Fact]
    public async Task Migration009_CreatesEveryPhase11Table()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        foreach (string table in new[]
        {
            "mfa_recovery_codes", "api_keys", "bundle_signing_identity", "bundle_trusted_signers", "bundle_imports",
        })
        {
            (await TableExists(db, table)).Should().BeTrue($"table '{table}' should exist");
        }
    }

    [Fact]
    public async Task Users_GetMfaColumns_DefaultingToDisabled()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await Exec(db, """
            INSERT INTO roles (role_id, name) VALUES (99, 'temp') ON CONFLICT DO NOTHING;
            INSERT INTO users (username, display_name, role_id, created_utc)
            VALUES ('mfa-test', 'MFA Test', 1, '2026-01-01T00:00:00.0000000Z');
            """);

        (await Scalar(db, "SELECT mfa_enabled FROM users WHERE username = 'mfa-test';")).Should().Be(0L);
    }

    [Fact]
    public async Task ApiKeys_KeyHashIsUnique()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, InsertApiKey("k1", "hash-a"));

        Func<Task> dup = () => Exec(db, InsertApiKey("k2", "hash-a"));
        await dup.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task BundleTrustedSigners_FingerprintIsThePrimaryKey()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, InsertTrustedSigner("AA:BB", "pub-1", "First"));
        await Exec(db, "UPDATE bundle_trusted_signers SET label = 'Second' WHERE fingerprint = 'AA:BB';");

        (await Scalar(db, "SELECT COUNT(*) FROM bundle_trusted_signers;")).Should().Be(1L);
        (await Scalar(db, "SELECT label FROM bundle_trusted_signers WHERE fingerprint = 'AA:BB';")).Should().Be("Second");
    }

    [Fact]
    public async Task MfaRecoveryCodes_CascadeDeleteWithTheirUser()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await Exec(db, "PRAGMA foreign_keys = ON;");
        await Exec(db, "INSERT INTO users (username, display_name, role_id, created_utc) VALUES ('cascade-test', 'x', 1, 't');");
        long userId = Convert.ToInt64(await Scalar(db, "SELECT user_id FROM users WHERE username = 'cascade-test';"));
        await Exec(db, $"INSERT INTO mfa_recovery_codes (user_id, code_hash, created_utc) VALUES ({userId}, 'h', 't');");

        await Exec(db, $"DELETE FROM users WHERE user_id = {userId};");

        (await Scalar(db, "SELECT COUNT(*) FROM mfa_recovery_codes;")).Should().Be(0L);
    }

    private static string InsertApiKey(string label, string hash) => $"""
        INSERT INTO api_keys (label, key_hash, purpose, created_utc) VALUES ('{label}', '{hash}', 'wineventlog', 't');
        """;

    private static string InsertTrustedSigner(string fingerprint, string publicKey, string label) => $"""
        INSERT INTO bundle_trusted_signers (fingerprint, public_key, label, trusted_utc, trusted_by)
        VALUES ('{fingerprint}', '{publicKey}', '{label}', 't', 'admin');
        """;

    private static async Task<bool> TableExists(SqliteTestDatabase db, string table)
    {
        object? result = await Scalar(db, $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}';");
        return result is string;
    }

    private static async Task<object?> Scalar(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
