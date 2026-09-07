using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 build item 6 and the "Audit immutability" validation item: the audit log is
/// append-only in fact (triggers + no write API), carries before/after on config changes,
/// and is tamper-evident via its hash chain.
/// </summary>
public sealed class AuditLogTests
{
    private static SqliteAuditLog NewLog(SqliteTestDatabase db) =>
        new(db.Factory, new FakeTimeProvider(new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task AppendAsync_RecordsActorActionEntityAndSourceIp()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SqliteAuditLog log = NewLog(db);

        await log.AppendAsync(
            new AuditEntry(AuditActions.UserCreate, Actor: "admin", EntityType: "user", EntityId: "42", SourceIp: "10.1.2.3"),
            CancellationToken.None);

        IReadOnlyList<AuditRecord> rows = await log.QueryAsync(new AuditQuery(), CancellationToken.None);
        rows.Should().ContainSingle();
        rows[0].Actor.Should().Be("admin");
        rows[0].Action.Should().Be(AuditActions.UserCreate);
        rows[0].EntityId.Should().Be("42");
        rows[0].SourceIp.Should().Be("10.1.2.3");
    }

    [Fact]
    public async Task AppendAsync_ForAConfigChange_StoresRedactedBeforeAndAfter()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SqliteAuditLog log = NewLog(db);

        string? before = AuditDiff.Snapshot(new { Port = 514, Password = "old-secret" });
        string? after = AuditDiff.Snapshot(new { Port = 1514, Password = "new-secret" });
        await log.AppendAsync(
            new AuditEntry(AuditActions.ConfigChange, Actor: "admin", EntityType: "listener", EntityId: "1",
                BeforeJson: before, AfterJson: after),
            CancellationToken.None);

        AuditRecord row = (await log.QueryAsync(new AuditQuery(), CancellationToken.None))[0];
        row.BeforeJson.Should().Contain("514").And.NotContain("old-secret");
        row.AfterJson.Should().Contain("1514").And.NotContain("new-secret");
        AuditDiff.ChangedKeys(row.BeforeJson, row.AfterJson).Should().Equal("Port");
    }

    [Fact]
    public async Task AppendAsync_LinksEachEntryToItsPredecessorHash()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SqliteAuditLog log = NewLog(db);

        for (int i = 0; i < 5; i++)
        {
            await log.AppendAsync(new AuditEntry(AuditActions.LoginSuccess, Actor: $"u{i}"), CancellationToken.None);
        }

        AuditChainVerification result = await log.VerifyChainAsync(CancellationToken.None);
        result.Intact.Should().BeTrue();
        result.EntriesChecked.Should().Be(5);
    }

    [Fact]
    public async Task VerifyChainAsync_AfterAnOutOfBandRowEdit_ReportsTheBrokenLink()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SqliteAuditLog log = NewLog(db);
        await log.AppendAsync(new AuditEntry(AuditActions.LoginSuccess, Actor: "one"), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(AuditActions.ConfigChange, Actor: "two", Detail: "raised retention"), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(AuditActions.LoginSuccess, Actor: "three"), CancellationToken.None);

        // Simulate a process with direct file access dropping the guard triggers and
        // rewriting history. The hash chain must still catch it.
        await using (SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None))
        await using (SqliteCommand tamper = connection.CreateCommand())
        {
            tamper.CommandText = """
                DROP TRIGGER audit_log_no_update;
                UPDATE audit_log SET detail = 'lowered retention' WHERE actor = 'two';
                """;
            await tamper.ExecuteNonQueryAsync();
        }

        AuditChainVerification result = await log.VerifyChainAsync(CancellationToken.None);
        result.Intact.Should().BeFalse();
        result.FirstBrokenAuditId.Should().Be(2);
    }

    [Theory]
    [InlineData("UPDATE audit_log SET action = 'tampered'")]
    [InlineData("DELETE FROM audit_log")]
    public async Task RawSql_UpdateOrDelete_IsRejectedByTheTriggers(string sql)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SqliteAuditLog log = NewLog(db);
        await log.AppendAsync(new AuditEntry(AuditActions.LoginSuccess, Actor: "x"), CancellationToken.None);

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        (await ((Func<Task>)(() => command.ExecuteNonQueryAsync())).Should().ThrowAsync<SqliteException>())
            .Which.Message.Should().ContainEquivalentOf("append-only");
    }

    [Fact]
    public void SqliteAuditLog_ExposesNoUpdateOrDeleteMethod()
    {
        IEnumerable<string> methods = typeof(SqliteAuditLog)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name);

        methods.Should().NotContain(n =>
            n.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Purge", StringComparison.OrdinalIgnoreCase));
    }
}
