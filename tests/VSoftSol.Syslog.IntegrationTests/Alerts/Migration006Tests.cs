using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>Migration 006 — the Phase 8 alert schema (ADR 0016).</summary>
public sealed class Migration006Tests
{
    [Fact]
    public async Task Migration006_CreatesEveryAlertTable()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        foreach (string table in new[]
                 {
                     "alert_definitions", "alert_instances", "alert_instance_events",
                     "alert_transitions", "alert_eval_runs", "alert_action_queue",
                 })
        {
            (await TableExists(db, table)).Should().BeTrue(table);
        }
    }

    [Fact]
    public async Task AlertInstances_PartialUniqueIndex_AllowsOnlyOneOpenInstancePerGroup()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, """
            INSERT INTO alert_definitions (name, eval_type, window_seconds, interval_seconds, created_utc, updated_utc)
            VALUES ('a', 'threshold', 60, 60, 't', 't');
            """);

        await Exec(db, InsertInstance("core-sw-1", "firing"));

        Func<Task> secondOpen = () => Exec(db, InsertInstance("core-sw-1", "acknowledged"));
        await secondOpen.Should().ThrowAsync<SqliteException>("the partial unique index forbids a second open instance for the same group");

        // once the first is resolved, a new one may open
        await Exec(db, "UPDATE alert_instances SET state = 'resolved' WHERE group_value = 'core-sw-1';");
        await Exec(db, InsertInstance("core-sw-1", "firing"));

        (await Scalar(db, "SELECT COUNT(*) FROM alert_instances WHERE group_value = 'core-sw-1';")).Should().Be(2L);
    }

    [Fact]
    public async Task AlertInstances_DifferentGroups_BothOpenFreely()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, """
            INSERT INTO alert_definitions (name, eval_type, window_seconds, interval_seconds, created_utc, updated_utc)
            VALUES ('a', 'threshold', 60, 60, 't', 't');
            """);

        await Exec(db, InsertInstance("core-sw-1", "firing"));
        await Exec(db, InsertInstance("core-sw-2", "firing"));

        (await Scalar(db, "SELECT COUNT(*) FROM alert_instances WHERE state = 'firing';")).Should().Be(2L);
    }

    [Fact]
    public async Task AlertActionQueue_UniqueOnInstanceActionSeq_MakesReEnqueueIdempotent()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, """
            INSERT INTO alert_definitions (name, eval_type, window_seconds, interval_seconds, created_utc, updated_utc)
            VALUES ('a', 'threshold', 60, 60, 't', 't');
            """);
        await Exec(db, InsertInstance("g", "firing"));

        for (int i = 0; i < 2; i++)
        {
            await Exec(db, """
                INSERT OR IGNORE INTO alert_action_queue (alert_id, instance_id, action_index, notify_seq, kind, payload_json, next_attempt_utc, created_utc)
                VALUES (1, 1, 0, 0, 'notify', '{}', 't', 't');
                """);
        }

        (await Scalar(db, "SELECT COUNT(*) FROM alert_action_queue;")).Should().Be(1L);

        // a later re-notify round (seq 1) is a distinct row
        await Exec(db, """
            INSERT OR IGNORE INTO alert_action_queue (alert_id, instance_id, action_index, notify_seq, kind, payload_json, next_attempt_utc, created_utc)
            VALUES (1, 1, 0, 1, 'notify', '{}', 't', 't');
            """);
        (await Scalar(db, "SELECT COUNT(*) FROM alert_action_queue;")).Should().Be(2L);
    }

    [Fact]
    public async Task AlertDefinitions_EvalTypeCheck_RejectsAnUnknownType()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        Func<Task> bad = () => Exec(db, """
            INSERT INTO alert_definitions (name, eval_type, window_seconds, interval_seconds, created_utc, updated_utc)
            VALUES ('a', 'telepathy', 60, 60, 't', 't');
            """);
        await bad.Should().ThrowAsync<SqliteException>();
    }

    private static string InsertInstance(string group, string state) => $"""
        INSERT INTO alert_instances (alert_id, group_value, state, severity, observed_value, threshold, opened_utc)
        VALUES (1, '{group}', '{state}', 'warning', 10, 5, 't');
        """;

    private static async Task<bool> TableExists(SqliteTestDatabase db, string name)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
