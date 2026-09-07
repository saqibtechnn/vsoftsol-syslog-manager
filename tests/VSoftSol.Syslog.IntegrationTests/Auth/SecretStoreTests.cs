using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 build item 8 and the "Secret leakage scan" validation item: DPAPI-protected
/// secret storage; plaintext never in the database, the name list, or an audit diff.
/// The product is Windows-only (on-premises Windows service); DPAPI is the v1 protector.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStoreTests
{
    private const string Plaintext = "smtp-Pa55word-do-not-leak";

    private static SqliteSecretStore NewStore(SqliteTestDatabase db) =>
        new(db.Factory, new DpapiSecretProtector(),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task SetThenGet_RoundTripsThePlaintext()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteSecretStore store = NewStore(db);

        await store.SetAsync("smtp.password", Plaintext, updatedBy: "admin", CancellationToken.None);

        (await store.GetAsync("smtp.password", CancellationToken.None)).Should().Be(Plaintext);
    }

    [Fact]
    public async Task StoredValue_IsCiphertext_NotThePlaintextBytes()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteSecretStore store = NewStore(db);
        await store.SetAsync("webhook.token", Plaintext, updatedBy: null, CancellationToken.None);

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT protected_value FROM secrets WHERE name = 'webhook.token';";
        var blob = (byte[])(await command.ExecuteScalarAsync())!;

        Encoding.UTF8.GetString(blob).Should().NotContain(Plaintext);
        blob.Should().NotEqual(Encoding.UTF8.GetBytes(Plaintext));
    }

    [Fact]
    public async Task ListNames_ReturnsNamesOnly_NeverValues()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteSecretStore store = NewStore(db);
        await store.SetAsync("a.secret", Plaintext, null, CancellationToken.None);
        await store.SetAsync("b.secret", "another-value", null, CancellationToken.None);

        IReadOnlyList<string> names = await store.ListNamesAsync(CancellationToken.None);

        names.Should().Equal("a.secret", "b.secret");
        names.Should().NotContain(n => n.Contains(Plaintext, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Set_OverwritesAnExistingSecret()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteSecretStore store = NewStore(db);
        await store.SetAsync("rotating", "v1", null, CancellationToken.None);

        await store.SetAsync("rotating", "v2", null, CancellationToken.None);

        (await store.GetAsync("rotating", CancellationToken.None)).Should().Be("v2");
    }

    [Fact]
    public async Task Delete_RemovesTheSecret()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteSecretStore store = NewStore(db);
        await store.SetAsync("temp", Plaintext, null, CancellationToken.None);

        (await store.DeleteAsync("temp", CancellationToken.None)).Should().BeTrue();
        (await store.GetAsync("temp", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LeakScan_SecretConfiguredAndConfigChangeAudited_NoPlaintextInDatabaseOrAudit()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SqliteSecretStore store = NewStore(db);
        var audit = new SqliteAuditLog(db.Factory, new FakeTimeProvider());

        await store.SetAsync("smtp.password", Plaintext, "admin", CancellationToken.None);
        await audit.AppendAsync(
            new AuditEntry(
                AuditActions.ConfigChange,
                Actor: "admin",
                EntityType: "smtp",
                BeforeJson: AuditDiff.Snapshot(new { Host = "old", Password = "prev-secret" }),
                AfterJson: AuditDiff.Snapshot(new { Host = "new", Password = Plaintext })),
            CancellationToken.None);

        string dump = await DumpEveryTextColumnAsync(db);
        dump.Should().NotContain(Plaintext);
        dump.Should().NotContain("prev-secret");
    }

    private static async Task<string> DumpEveryTextColumnAsync(SqliteTestDatabase db)
    {
        var sb = new StringBuilder();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);

        var tables = new List<string>();
        await using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using SqliteDataReader reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (string table in tables)
        {
            await using SqliteCommand rows = connection.CreateCommand();
            rows.CommandText = $"SELECT * FROM \"{table}\";";
            await using SqliteDataReader reader = await rows.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (!reader.IsDBNull(i) && reader.GetValue(i) is string s)
                    {
                        sb.Append(s).Append('\n');
                    }
                }
            }
        }

        return sb.ToString();
    }
}
