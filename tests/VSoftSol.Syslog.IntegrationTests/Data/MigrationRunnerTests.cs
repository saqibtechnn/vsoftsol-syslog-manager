using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task MigrateAsync_OnEmptyFile_CreatesEverySchemaObject()
    {
        await using SqliteTestDatabase db = SqliteTestDatabase.CreateUnmigrated();

        int applied = await db.Runner.MigrateAsync(CancellationToken.None);

        applied.Should().Be(db.Runner.Migrations.Count);
        (await TableNames(db)).Should().Contain(new[]
        {
            "events", "event_fields", "devices", "device_groups", "listeners", "streams",
            "rules", "users", "roles", "audit_log", "schema_version", "events_fts",
        });
    }

    [Fact]
    public async Task MigrateAsync_RunTwice_SecondRunAppliesNothing()
    {
        await using SqliteTestDatabase db = SqliteTestDatabase.CreateUnmigrated();

        int first = await db.Runner.MigrateAsync(CancellationToken.None);
        int second = await db.Runner.MigrateAsync(CancellationToken.None);

        first.Should().BeGreaterThan(0);
        second.Should().Be(0);
    }

    [Fact]
    public async Task MigrateAsync_WhenAnAppliedMigrationChanged_Throws()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        var tampered = new List<Migration>
        {
            new(1, "initial", db.Runner.Migrations[0].Sql + "\n-- edited after the fact\n"),
        };
        var drifted = new MigrationRunner(db.Factory, NullLogger<MigrationRunner>.Instance, tampered);

        Func<Task> act = () => drifted.MigrateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<MigrationException>().WithMessage("*changed since it was applied*");
    }

    [Fact]
    public async Task MigrateAsync_OnDatabaseWith100kRows_PreservesEveryRow()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        var batch = new List<Core.Events.SyslogEvent>(100_000);
        for (int i = 0; i < 100_000; i++)
        {
            batch.Add(SampleEvents.Minimal($"row {i}"));
        }

        await db.Repository.AppendBatchAsync(batch, CancellationToken.None);
        (long countBefore, long checksumBefore) = await Fingerprint(db);

        // Re-running the runner is the only forward operation available; it must be a no-op.
        await db.Runner.MigrateAsync(CancellationToken.None);

        (long countAfter, long checksumAfter) = await Fingerprint(db);
        countAfter.Should().Be(countBefore).And.Be(100_000);
        checksumAfter.Should().Be(checksumBefore);
    }

    private static async Task<HashSet<string>> TableNames(SqliteTestDatabase db)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table');";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<(long Count, long Checksum)> Fingerprint(SqliteTestDatabase db)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), COALESCE(SUM(event_id), 0) FROM events;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
