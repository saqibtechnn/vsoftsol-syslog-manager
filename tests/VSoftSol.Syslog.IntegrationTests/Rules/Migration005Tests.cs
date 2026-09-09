using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>Migration 005 — the Phase 7 rules / action-outbox / notification schema.</summary>
public sealed class Migration005Tests
{
    [Fact]
    public async Task Migration005_AddsTheRuleColumnsAndTables()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await ColumnExists(db, "rules", "time_window_json")).Should().BeTrue();
        (await ColumnExists(db, "rules", "escalation_json")).Should().BeTrue();
        (await ColumnExists(db, "rules", "device_group_ids")).Should().BeTrue();
        (await ColumnExists(db, "rules", "hit_count")).Should().BeTrue();
        (await ColumnExists(db, "rules", "last_fired_utc")).Should().BeTrue();
        (await ColumnExists(db, "rules", "is_system")).Should().BeTrue();
        (await TableExists(db, "rule_action_queue")).Should().BeTrue();
        (await TableExists(db, "notifications")).Should().BeTrue();
    }

    [Fact]
    public async Task ActionQueue_UniqueOnRuleEventAction_MakesReEnqueueIdempotent()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, "INSERT INTO rules (name, priority, created_utc, updated_utc) VALUES ('r', 100, 't', 't');");
        await db.Repository.AppendBatchAsync([SampleEvents.Minimal()], CancellationToken.None);

        await Exec(db, """
            INSERT OR IGNORE INTO rule_action_queue (rule_id, action_index, event_id, kind, payload_json, next_attempt_utc, created_utc)
            VALUES (1, 0, 1, 'notify', '{}', 't', 't');
            """);
        // second enqueue of the same (rule, event, action) is ignored
        await Exec(db, """
            INSERT OR IGNORE INTO rule_action_queue (rule_id, action_index, event_id, kind, payload_json, next_attempt_utc, created_utc)
            VALUES (1, 0, 1, 'notify', '{}', 't', 't');
            """);

        (await Scalar(db, "SELECT COUNT(*) FROM rule_action_queue;")).Should().Be(1L);
    }

    [Fact]
    public async Task ActionQueue_StateCheckConstraint_RejectsAnUnknownState()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await Exec(db, "INSERT INTO rules (name, priority, created_utc, updated_utc) VALUES ('r', 100, 't', 't');");
        await db.Repository.AppendBatchAsync([SampleEvents.Minimal()], CancellationToken.None);

        Func<Task> bad = () => Exec(db, """
            INSERT INTO rule_action_queue (rule_id, action_index, event_id, kind, payload_json, state, next_attempt_utc, created_utc)
            VALUES (1, 0, 1, 'notify', '{}', 'exploded', 't', 't');
            """);
        await bad.Should().ThrowAsync<SqliteException>();
    }

    private static async Task<bool> TableExists(SqliteTestDatabase db, string name)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> ColumnExists(SqliteTestDatabase db, string table, string column)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
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
