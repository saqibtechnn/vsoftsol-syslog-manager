using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 "Scope filter test" and the "Scope-bypass suite": a user scoped to stream A
/// cannot reach an event in stream B by direct id, by query parameter, by paging, or by
/// the context view.
/// </summary>
public sealed class ScopedEventReaderTests
{
    [Fact]
    public async Task GetByIdAsync_ForAnOutOfScopeEvent_ReturnsNull()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (long inA, long inB) = await SeedTwoStreamEventsAsync(db);
        UserScope scopedToA = await StreamScopeAsync(db, "Stream A");

        (await reader.GetByIdAsync(scopedToA, inA, CancellationToken.None)).Should().NotBeNull();
        (await reader.GetByIdAsync(scopedToA, inB, CancellationToken.None)).Should().BeNull("event in stream B is out of scope");
    }

    [Fact]
    public async Task QueryAsync_ReturnsOnlyInScopeEvents()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (long inA, _) = await SeedTwoStreamEventsAsync(db);
        UserScope scopedToA = await StreamScopeAsync(db, "Stream A");

        List<long> ids = await CollectIdsAsync(reader.QueryAsync(scopedToA, new LogQuery(), CancellationToken.None));

        ids.Should().Equal(inA);
    }

    [Fact]
    public async Task QueryAsync_WhenCallerFiltersToAnOutOfScopeStream_ReturnsNothing()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        _ = await SeedTwoStreamEventsAsync(db);
        UserScope scopedToA = await StreamScopeAsync(db, "Stream A");
        long streamB = await StreamIdAsync(db, "Stream B");

        var craftedQuery = new LogQuery { StreamIds = [streamB] };
        List<long> ids = await CollectIdsAsync(reader.QueryAsync(scopedToA, craftedQuery, CancellationToken.None));

        ids.Should().BeEmpty("a caller cannot widen scope by asking for another stream directly");
    }

    [Fact]
    public async Task CountAsync_CountsOnlyInScopeEvents()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        _ = await SeedTwoStreamEventsAsync(db);
        UserScope scopedToA = await StreamScopeAsync(db, "Stream A");

        (await reader.CountAsync(scopedToA, new LogQuery(), CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task GetContextAsync_FromAnOutOfScopeAnchor_ReturnsEmpty()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (_, long inB) = await SeedTwoStreamEventsAsync(db);
        UserScope scopedToA = await StreamScopeAsync(db, "Stream A");

        (await reader.GetContextAsync(scopedToA, inB, 5, 5, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_DeviceGroupScoped_ReturnsOnlyEventsFromDevicesInTheGroup()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);

        long groupIn = await ExecScalarAsync(db, "INSERT INTO device_groups (name, created_utc) VALUES ('In','t'); SELECT last_insert_rowid();");
        long deviceIn = await ExecScalarAsync(db, "INSERT INTO devices (name, created_utc) VALUES ('d-in','t'); SELECT last_insert_rowid();");
        long deviceOut = await ExecScalarAsync(db, "INSERT INTO devices (name, created_utc) VALUES ('d-out','t'); SELECT last_insert_rowid();");
        await ExecAsync(db, $"INSERT INTO device_group_members (group_id, device_id) VALUES ({groupIn}, {deviceIn});");

        long evtIn = await db.Repository.AppendAsync(EventWithDevice("in", deviceIn), CancellationToken.None);
        _ = await db.Repository.AppendAsync(EventWithDevice("out", deviceOut), CancellationToken.None);

        UserScope groupScope = UserScope.Create(streamIds: null, deviceGroupIds: [groupIn]);
        List<long> ids = await CollectIdsAsync(reader.QueryAsync(groupScope, new LogQuery(), CancellationToken.None));

        ids.Should().Equal(evtIn);
    }

    [Fact]
    public async Task QueryAsync_UnrestrictedScope_SeesEverything()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (long inA, long inB) = await SeedTwoStreamEventsAsync(db);

        List<long> ids = await CollectIdsAsync(reader.QueryAsync(UserScope.Unrestricted, new LogQuery(), CancellationToken.None));

        ids.Should().BeEquivalentTo(new[] { inA, inB });
    }

    // ----- helpers -----

    private static async Task<(long InA, long InB)> SeedTwoStreamEventsAsync(SqliteTestDatabase db)
    {
        await ExecAsync(db, "INSERT INTO streams (name, created_utc) VALUES ('Stream A','t'), ('Stream B','t');");
        long a = await db.Repository.AppendAsync(SampleEvents.Minimal("in A"), CancellationToken.None);
        long b = await db.Repository.AppendAsync(SampleEvents.Minimal("in B"), CancellationToken.None);
        long streamA = await StreamIdAsync(db, "Stream A");
        long streamB = await StreamIdAsync(db, "Stream B");
        await ExecAsync(db, $"INSERT INTO event_streams (event_id, stream_id) VALUES ({a},{streamA}), ({b},{streamB});");
        return (a, b);
    }

    private static SyslogEvent EventWithDevice(string message, long deviceId) => new()
    {
        ReceivedUtc = DateTimeOffset.UtcNow,
        SourceIp = "192.0.2.50",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Raw,
        DeviceId = deviceId,
    };

    private static async Task<UserScope> StreamScopeAsync(SqliteTestDatabase db, string streamName) =>
        UserScope.Create(streamIds: [await StreamIdAsync(db, streamName)], deviceGroupIds: null);

    private static Task<long> StreamIdAsync(SqliteTestDatabase db, string name) =>
        ExecScalarAsync(db, $"SELECT stream_id FROM streams WHERE name = '{name}';");

    private static async Task<List<long>> CollectIdsAsync(IAsyncEnumerable<SyslogEvent> events)
    {
        var ids = new List<long>();
        await foreach (SyslogEvent evt in events)
        {
            ids.Add(evt.EventId);
        }

        return ids;
    }

    private static async Task ExecAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecScalarAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
