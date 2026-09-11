using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>Migration 008 — the Phase 10 retention/archive/report schema.</summary>
public sealed class Migration008Tests
{
    [Fact]
    public async Task Migration008_CreatesEveryPhase10Table()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        foreach (string table in new[]
        {
            "retention_settings", "retention_policies", "archives", "archive_restores",
            "tiering_checkpoints", "reports", "report_runs", "report_smtp_settings",
        })
        {
            (await TableExists(db, table)).Should().BeTrue($"table '{table}' should exist");
        }
    }

    [Fact]
    public async Task Events_GetsATierColumn_DefaultingToHot()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await Exec(db, """
            INSERT INTO events (received_utc, source_ip, facility, severity, protocol, parse_status, raw_message)
            VALUES ('2026-01-01T00:00:00.0000000Z', '10.0.0.1', 1, 6, 'udp', 'raw', x'00');
            """);

        (await Scalar(db, "SELECT tier FROM events;")).Should().Be("hot");
    }

    [Fact]
    public async Task Events_TierColumn_RejectsAnyValueOtherThanHotOrWarm()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        Func<Task> act = () => Exec(db, """
            INSERT INTO events (received_utc, source_ip, facility, severity, protocol, parse_status, raw_message, tier)
            VALUES ('2026-01-01T00:00:00.0000000Z', '10.0.0.1', 1, 6, 'udp', 'raw', x'00', 'cold');
            """);

        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task RetentionSettings_SeededAsOneRow_WithProductDefaults()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await Scalar(db, "SELECT COUNT(*) FROM retention_settings;")).Should().Be(1L);
        (await Scalar(db, "SELECT default_hot_days FROM retention_settings;")).Should().Be(30L);
        (await Scalar(db, "SELECT default_warm_days FROM retention_settings;")).Should().Be(90L);
        (await Scalar(db, "SELECT default_cold_days FROM retention_settings;")).Should().Be(365L);
    }

    [Fact]
    public async Task TieringCheckpoints_SeededForAllFourPhases_AtZero()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await Scalar(db, "SELECT COUNT(*) FROM tiering_checkpoints;")).Should().Be(4L);
        (await Scalar(db, "SELECT last_event_id FROM tiering_checkpoints WHERE phase = 'warm';")).Should().Be(0L);
        (await Scalar(db, "SELECT last_event_id FROM tiering_checkpoints WHERE phase = 'cold';")).Should().Be(0L);
    }

    [Fact]
    public async Task Archives_FilePathIsUnique()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, InsertArchive("a1", "/archives/x.vsarc"));

        Func<Task> dup = () => Exec(db, InsertArchive("a2", "/archives/x.vsarc"));
        await dup.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Reports_SystemTemplateKeyIsUniqueAmongSystemRows_ButNotAcrossOwnedRows()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, "INSERT INTO reports (name, template_key, is_system, created_utc) VALUES ('a', 'canned:x', 1, 't');");

        Func<Task> dupSystem = () => Exec(db, "INSERT INTO reports (name, template_key, is_system, created_utc) VALUES ('b', 'canned:x', 1, 't');");
        await dupSystem.Should().ThrowAsync<SqliteException>();

        // Two custom (non-system) reports sharing template_key='custom' must NOT collide.
        Func<Task> twoCustom = async () =>
        {
            await Exec(db, "INSERT INTO reports (name, template_key, is_system, created_utc) VALUES ('c1', 'custom', 0, 't');");
            await Exec(db, "INSERT INTO reports (name, template_key, is_system, created_utc) VALUES ('c2', 'custom', 0, 't');");
        };
        await twoCustom.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Seeder_InsertsTheElevenDefaultReports_Idempotently()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Seeder.SeedAsync(CancellationToken.None);
        await db.Seeder.SeedAsync(CancellationToken.None);

        (await Scalar(db, "SELECT COUNT(*) FROM reports WHERE is_system = 1;")).Should().Be(11L);
    }

    [Fact]
    public async Task ReportSmtpSettings_SeededAsOneRow()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        (await Scalar(db, "SELECT COUNT(*) FROM report_smtp_settings;")).Should().Be(1L);
    }

    private static string InsertArchive(string streamName, string path) => $"""
        INSERT INTO archives (stream_name, file_path, period_start_utc, period_end_utc, event_count, byte_size, sha256, created_utc)
        VALUES ('{streamName}', '{path}', 't1', 't2', 0, 0, 'h', 't');
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
