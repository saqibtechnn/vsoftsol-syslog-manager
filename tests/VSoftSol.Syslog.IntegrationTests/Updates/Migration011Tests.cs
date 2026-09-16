using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

/// <summary>Migration 011 — self-update settings (v1.1, ADR 0021).</summary>
[Trait("Category", "Updates")]
public sealed class Migration011Tests
{
    [Fact]
    public async Task Migration011_CreatesTheUpdateSettingsTable_SeededDisabled()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT check_enabled, check_interval_hours FROM update_settings WHERE id = 1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        (await reader.ReadAsync(CancellationToken.None)).Should().BeTrue("the default row must be seeded by the migration itself");
        reader.GetInt64(0).Should().Be(0, "self-update checking is off by default");
        reader.GetInt32(1).Should().Be(24);
    }

    [Fact]
    public async Task CheckIntervalHours_OutOfRange_IsRejectedByTheCheckConstraint()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        Func<Task> tooLow = () => Exec(db, "UPDATE update_settings SET check_interval_hours = 0 WHERE id = 1;");
        await tooLow.Should().ThrowAsync<SqliteException>();

        Func<Task> tooHigh = () => Exec(db, "UPDATE update_settings SET check_interval_hours = 200 WHERE id = 1;");
        await tooHigh.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Id_MustBeOne_OnlyASingletonRowIsAllowed()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        Func<Task> secondRow = () => Exec(db, "INSERT INTO update_settings (id) VALUES (2);");
        await secondRow.Should().ThrowAsync<SqliteException>();
    }

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
