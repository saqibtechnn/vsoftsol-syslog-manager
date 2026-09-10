using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>Migration 007 — the Phase 9 dashboard schema (ADR 0017).</summary>
public sealed class Migration007Tests
{
    [Fact]
    public async Task Migration007_CreatesTheDashboardTables()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await TableExists(db, "dashboards")).Should().BeTrue();
        (await TableExists(db, "collector_stat_samples")).Should().BeTrue();
    }

    [Fact]
    public async Task Dashboards_ASystemRowMustHaveNoOwner_AUserRowMustHaveOne()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        Func<Task> systemWithOwner = () => Exec(db,
            "INSERT INTO dashboards (owner_user_id, name, is_system, created_utc, updated_utc) VALUES (1, 'x', 1, 't', 't');");
        await systemWithOwner.Should().ThrowAsync<SqliteException>();

        Func<Task> userWithoutOwner = () => Exec(db,
            "INSERT INTO dashboards (owner_user_id, name, is_system, created_utc, updated_utc) VALUES (NULL, 'y', 0, 't', 't');");
        await userWithoutOwner.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Dashboards_SystemKeyIsUnique()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, "INSERT INTO dashboards (owner_user_id, name, is_system, system_key, created_utc, updated_utc) VALUES (NULL, 'a', 1, 'k', 't', 't');");

        Func<Task> dup = () => Exec(db, "INSERT INTO dashboards (owner_user_id, name, is_system, system_key, created_utc, updated_utc) VALUES (NULL, 'b', 1, 'k', 't', 't');");
        await dup.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Dashboards_DeletingTheOwner_CascadesTheDashboard()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        long uid = await Scalar(db, "SELECT user_id FROM users WHERE username = 'admin';") is long l ? l : 0;
        await Exec(db, $"INSERT INTO dashboards (owner_user_id, name, is_system, created_utc, updated_utc) VALUES ({uid}, 'mine', 0, 't', 't');");

        await Exec(db, $"DELETE FROM users WHERE user_id = {uid};");

        (await Scalar(db, "SELECT COUNT(*) FROM dashboards WHERE name = 'mine';")).Should().Be(0L);
    }

    [Fact]
    public async Task Seeder_InsertsTheFourDefaultDashboards_Idempotently()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Seeder.SeedAsync(CancellationToken.None);
        await db.Seeder.SeedAsync(CancellationToken.None);

        (await Scalar(db, "SELECT COUNT(*) FROM dashboards WHERE is_system = 1;")).Should().Be(4L);
        (await Scalar(db, "SELECT COUNT(*) FROM dashboards WHERE system_key = 'network-overview';")).Should().Be(1L);
    }

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
