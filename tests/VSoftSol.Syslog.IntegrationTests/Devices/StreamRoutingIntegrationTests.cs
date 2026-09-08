using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Data.Streams;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Streams;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Devices;

/// <summary>
/// Stream routing against the real seeded streams and through the real repository:
/// events are linked to the streams they match, at ingest, and the seven defaults route
/// representative messages sensibly.
/// </summary>
public sealed class StreamRoutingIntegrationTests
{
    private static SyslogEvent Evt(string message, Severity severity = Severity.Notice,
        ParseStatus parseStatus = ParseStatus.Rfc3164, string? app = null, string? msgId = null) => new()
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "10.0.0.1",
            Hostname = "core-sw-1",
            AppName = app,
            MsgId = msgId,
            Facility = Facility.Local7,
            Severity = severity,
            Protocol = Protocol.Udp,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = parseStatus,
        };

    private static async Task<StreamRouter> BuildRouterAsync(SqliteTestDatabase db)
    {
        var store = new SqliteStreamStore(db.Factory);
        IReadOnlyList<StreamRow> rows = await store.ListActiveAsync(CancellationToken.None);
        return StreamRouter.Build(rows.Select(r => new StreamDefinition(r.StreamId, r.Name, r.Enabled, r.IsCatchAll, r.Match)));
    }

    private static async Task<Dictionary<string, long>> StreamIdsByNameAsync(SqliteTestDatabase db)
    {
        var map = new Dictionary<string, long>();
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, stream_id FROM streams;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    [Fact]
    public async Task SeededDefaultStreams_CompileWithoutError()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        StreamRouter router = await BuildRouterAsync(db);

        router.CompileErrors.Should().BeEmpty();
        router.StreamCount.Should().Be(7);
    }

    [Theory]
    [InlineData("Interface GigabitEthernet0/1, changed state to down", "Interface Up/Down")]
    [InlineData("authentication failure for user root", "Authentication Failures")]
    [InlineData("%SYS-5-CONFIG_I: Configured from console by admin", "Configuration Changes")]
    [InlineData("Power supply 1 failure detected", "Hardware/Environment")]
    [InlineData("access denied for 10.0.0.9", "Security Events")]
    public async Task DefaultStreamRules_RouteRepresentativeMessages(string message, string expectedStream)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        StreamRouter router = await BuildRouterAsync(db);
        Dictionary<string, long> ids = await StreamIdsByNameAsync(db);

        IReadOnlyList<long> routed = router.Route(Evt(message));

        routed.Should().Contain(ids["All Messages"]);
        routed.Should().Contain(ids[expectedStream]);
    }

    [Fact]
    public async Task RawMessage_RoutesToParseFailures_AndAllMessages()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        StreamRouter router = await BuildRouterAsync(db);
        Dictionary<string, long> ids = await StreamIdsByNameAsync(db);

        IReadOnlyList<long> routed = router.Route(Evt("garbled ~~~", parseStatus: ParseStatus.Raw));

        routed.Should().Contain(new[] { ids["All Messages"], ids["Parse Failures"] });
    }

    [Fact]
    public async Task RoutedEvent_PersistsItsStreamMemberships_AtIngest()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        StreamRouter router = await BuildRouterAsync(db);
        Dictionary<string, long> ids = await StreamIdsByNameAsync(db);

        SyslogEvent parsed = Evt("failed password for root");
        SyslogEvent routed = parsed.WithRouting(deviceId: null, streamIds: router.Route(parsed));
        long eventId = await db.Repository.AppendAsync(routed, CancellationToken.None);

        var linked = new List<long>();
        await using (SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None))
        {
            await using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT stream_id FROM event_streams WHERE event_id = $id;";
            cmd.Parameters.AddWithValue("$id", eventId);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                linked.Add(reader.GetInt64(0));
            }
        }

        linked.Should().Contain(new[] { ids["All Messages"], ids["Authentication Failures"] });
    }

    [Fact]
    public async Task StreamWithHostileRegexRule_IsDroppedFromRouting_ButOthersKeepWorking()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteStreamStore(db.Factory);

        // A rule that cannot compile (backreference) — the router must log + skip it.
        var badRule = new ConditionGroup
        {
            Join = ConditionJoin.Or,
            Children = [new ConditionComparison { Field = "message", Operator = ConditionOperator.Matches, Value = @"(\w+)\s\1\s\1" }],
        };
        await store.CreateAsync("Broken", null, badRule, "test", CancellationToken.None);

        StreamRouter router = await BuildRouterAsync(db);
        router.CompileErrors.Should().ContainSingle().Which.Name.Should().Be("Broken");

        // Routing still works for everything else.
        Dictionary<string, long> ids = await StreamIdsByNameAsync(db);
        router.Route(Evt("authentication failure")).Should().Contain(ids["Authentication Failures"]);
    }

    [Fact]
    public async Task Router_RebuildsWhenAStreamChanges()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteStreamStore(db.Factory);
        var provider = new VSoftSol.Syslog.Service.Hosting.StreamRouterProvider(store, NullLogger<VSoftSol.Syslog.Service.Hosting.StreamRouterProvider>.Instance);

        StreamRouter before = await provider.GetAsync(CancellationToken.None);
        int beforeCount = before.StreamCount;

        await store.CreateAsync("New Stream", null,
            new ConditionGroup { Join = ConditionJoin.Or, Children = [new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "widget" }] },
            "test", CancellationToken.None);

        StreamRouter after = await provider.GetAsync(CancellationToken.None);
        after.StreamCount.Should().Be(beforeCount + 1);
        after.Should().NotBeSameAs(before);
    }
}
