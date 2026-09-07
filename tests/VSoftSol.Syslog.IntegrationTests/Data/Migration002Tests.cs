using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>PHASE_04 migration 002 — authentication, scoped visibility, sessions, secrets.</summary>
public sealed class Migration002Tests
{
    [Fact]
    public async Task Migrate_CreatesTheAuthTables()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);

        (await TableNames(connection)).Should().Contain(new[] { "user_scopes", "user_sessions", "secrets" });
    }

    [Fact]
    public async Task Migrate_AddsPasswordLifecycleAndAuditChainColumns()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);

        (await Columns(connection, "users")).Should().Contain("password_changed_utc");
        (await Columns(connection, "audit_log")).Should().Contain(new[] { "prev_hash", "entry_hash" });
    }

    [Fact]
    public async Task UserScopes_RejectsARowTargetingNeitherAStreamNorAGroup()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO user_scopes (user_id, stream_id, device_group_id) VALUES (1, NULL, NULL);";

        Func<Task> act = () => command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<SqliteException>()).Which.Message.Should().ContainEquivalentOf("CHECK");
    }

    [Fact]
    public async Task UserScopes_RejectsARowTargetingBothAStreamAndAGroup()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await Exec(connection, "INSERT INTO device_groups (name, created_utc) VALUES ('g', 't');");
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO user_scopes (user_id, stream_id, device_group_id) VALUES (1, 1, 1);";

        Func<Task> act = () => command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<SqliteException>()).Which.Message.Should().ContainEquivalentOf("CHECK");
    }

    [Fact]
    public async Task UserSessions_CascadeDeleteWithTheUser()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await Exec(connection, "INSERT INTO users (username, display_name, role_id, created_utc) VALUES ('temp','temp',2,'t');");
        long userId = await Scalar(connection, "SELECT user_id FROM users WHERE username='temp';");
        await Exec(connection,
            $"INSERT INTO user_sessions (session_id, user_id, created_utc, last_seen_utc, absolute_expiry_utc) " +
            $"VALUES ('s1', {userId}, 't', 't', 't');");

        await Exec(connection, $"DELETE FROM users WHERE user_id = {userId};");

        (await Scalar(connection, "SELECT COUNT(*) FROM user_sessions WHERE session_id='s1';")).Should().Be(0);
    }

    private static async Task<HashSet<string>> TableNames(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<HashSet<string>> Columns(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task Exec(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
