using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class SeedDataTests
{
    [Fact]
    public async Task SeedAsync_CreatesTheFourRoles_TheAdmin_AndEightDefaultStreams()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);

        (await Names(connection, "SELECT name FROM roles ORDER BY role_id"))
            .Should().Equal(Enum.GetNames<Role>());

        // Phase 11 adds the reserved "collector.health" self-monitoring stream
        // (sort_order = 999, so it always sorts last) alongside the Phase 6 default seven.
        (await Names(connection, "SELECT name FROM streams ORDER BY sort_order"))
            .Should().Equal(
                "All Messages", "Security Events", "Interface Up/Down", "Authentication Failures",
                "Configuration Changes", "Hardware/Environment", "Parse Failures", "collector.health");

        await using SqliteCommand admin = connection.CreateCommand();
        admin.CommandText = """
            SELECT u.password_hash, u.must_change_password, r.name
            FROM users u JOIN roles r ON r.role_id = u.role_id
            WHERE u.username = $u;
            """;
        admin.Parameters.AddWithValue("$u", DatabaseSeeder.SeededAdminUsername);
        await using SqliteDataReader reader = await admin.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.IsDBNull(0).Should().BeTrue("the first-run wizard sets the password");
        reader.GetInt64(1).Should().Be(1);
        reader.GetString(2).Should().Be(nameof(Role.Administrator));
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        await db.Seeder.SeedAsync(CancellationToken.None);
        await db.Seeder.SeedAsync(CancellationToken.None);
        await db.Seeder.SeedAsync(CancellationToken.None);

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        (await Scalar(connection, "SELECT COUNT(*) FROM roles")).Should().Be(Enum.GetValues<Role>().Length);
        (await Scalar(connection, "SELECT COUNT(*) FROM streams")).Should().Be(8); // Phase 6's seven + Phase 11's reserved collector.health
        (await Scalar(connection, "SELECT COUNT(*) FROM users")).Should().Be(1);
    }

    private static async Task<List<string>> Names(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
