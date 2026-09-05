using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>
/// Each constraint is a promise; assert every one is enforced rather than silently
/// accepted (PHASE_01 "Constraint enforcement").
/// </summary>
public sealed class SchemaConstraintTests
{
    [Theory]
    [InlineData("INSERT INTO events (received_utc, source_ip, facility, severity, protocol, raw_message, parse_status) VALUES ('t','ip', 99, 0, 'udp', x'00', 'raw')", "CHECK", "facility out of range")]
    [InlineData("INSERT INTO events (received_utc, source_ip, facility, severity, protocol, raw_message, parse_status) VALUES ('t','ip', 0, 8, 'udp', x'00', 'raw')", "CHECK", "severity out of range")]
    [InlineData("INSERT INTO events (received_utc, source_ip, facility, severity, protocol, raw_message, parse_status) VALUES ('t','ip', 0, 0, 'carrier-pigeon', x'00', 'raw')", "CHECK", "unknown protocol")]
    [InlineData("INSERT INTO events (received_utc, source_ip, facility, severity, protocol, raw_message, parse_status) VALUES ('t','ip', 0, 0, 'udp', x'00', 'guesswork')", "CHECK", "unknown parse_status")]
    [InlineData("INSERT INTO events (received_utc, source_ip, facility, severity, protocol, parse_status) VALUES ('t','ip', 0, 0, 'udp', 'raw')", "NOT NULL", "raw_message is mandatory")]
    [InlineData("INSERT INTO events (source_ip, facility, severity, protocol, raw_message, parse_status) VALUES ('ip', 0, 0, 'udp', x'00', 'raw')", "NOT NULL", "received_utc is mandatory")]
    [InlineData("INSERT INTO events (received_utc, source_ip, facility, severity, protocol, listener_id, raw_message, parse_status) VALUES ('t','ip',0,0,'udp', 424242, x'00', 'raw')", "FOREIGN KEY", "listener_id must reference a listener")]
    [InlineData("INSERT INTO roles (name) VALUES ('Administrator'); INSERT INTO roles (name) VALUES ('Administrator')", "UNIQUE", "role name is unique")]
    [InlineData("INSERT INTO audit_log (occurred_utc, action) VALUES ('t','x'); UPDATE audit_log SET action='y'", "append-only", "audit_log rejects UPDATE")]
    [InlineData("INSERT INTO audit_log (occurred_utc, action) VALUES ('t','x'); DELETE FROM audit_log", "append-only", "audit_log rejects DELETE")]
    public async Task Constraint_IsEnforced(string sql, string expectedFragment, string because)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        Func<Task> act = () => command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<SqliteException>(because)).Which.Message
            .Should().ContainEquivalentOf(expectedFragment);
    }

    [Fact]
    public async Task DeletingAnEvent_CascadesToEventFieldsAndStreams()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        long id = await db.Repository.AppendAsync(SampleEvents.Full(), CancellationToken.None);

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await Exec(connection, $"INSERT INTO event_streams (event_id, stream_id) SELECT {id}, stream_id FROM streams LIMIT 1");
        await Exec(connection, $"DELETE FROM events WHERE event_id = {id}");

        (await Scalar(connection, $"SELECT COUNT(*) FROM event_fields WHERE event_id = {id}")).Should().Be(0);
        (await Scalar(connection, $"SELECT COUNT(*) FROM event_streams WHERE event_id = {id}")).Should().Be(0);
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
