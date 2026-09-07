using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// PHASE_05 security: "a scoped user's search cannot return out-of-scope events via any
/// operator" and "scope bypass via sort fields, wildcards, negation, and export parameters".
/// </summary>
public sealed class SearchScopeTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("secret")]                 // free text that only the B event contains
    [InlineData("host:core-sw-b")]         // field term targeting the B event
    [InlineData("host:core*")]             // wildcard
    [InlineData("NOT host:xyz")]           // negation that matches everything
    [InlineData("stream:\"Stream B\"")]    // naming the out-of-scope stream directly
    [InlineData("severity:<=debug")]       // code <= 7 → matches everything
    [InlineData("")]                        // match-all
    public async Task SearchAsync_ScopedToStreamA_NeverReturnsStreamBEvents(string query)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (long inA, long inB) = await SeedAsync(db);
        UserScope scopedToA = UserScope.Create(streamIds: [await StreamIdAsync(db, "Stream A")], deviceGroupIds: null);

        var request = new SearchRequest
        {
            QueryText = query,
            FromUtc = Base.AddDays(-1),
            ToUtc = Base.AddDays(1),
            Limit = 100,
        };

        SearchResult result = await reader.SearchAsync(scopedToA, request, CancellationToken.None);

        result.Ok.Should().BeTrue(because: result.Error);
        result.Rows.Select(r => r.EventId).Should().NotContain(inB);
        if (query is "" or "severity:<=debug" or "NOT host:xyz")
        {
            result.Rows.Select(r => r.EventId).Should().Contain(inA);
        }
    }

    [Fact]
    public async Task SearchStreamAsync_ForExport_IsAlsoScoped()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (long inA, long inB) = await SeedAsync(db);
        UserScope scopedToA = UserScope.Create(streamIds: [await StreamIdAsync(db, "Stream A")], deviceGroupIds: null);

        var request = new SearchRequest { QueryText = string.Empty, FromUtc = Base.AddDays(-1), ToUtc = Base.AddDays(1), Limit = 1000 };
        var ids = new List<long>();
        await foreach (SyslogEvent e in reader.SearchStreamAsync(scopedToA, request, CancellationToken.None))
        {
            ids.Add(e.EventId);
        }

        ids.Should().Contain(inA).And.NotContain(inB);
    }

    [Fact]
    public async Task PollLiveAsync_IsScoped()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        (long inA, long inB) = await SeedAsync(db);
        UserScope scopedToA = UserScope.Create(streamIds: [await StreamIdAsync(db, "Stream A")], deviceGroupIds: null);

        var request = new SearchRequest { QueryText = string.Empty, FromUtc = Base.AddDays(-1), ToUtc = Base.AddDays(1) };
        IReadOnlyList<SyslogEvent> live = await reader.PollLiveAsync(scopedToA, request, afterEventId: 0, CancellationToken.None);

        live.Select(e => e.EventId).Should().Contain(inA).And.NotContain(inB);
    }

    private static async Task<(long InA, long InB)> SeedAsync(SqliteTestDatabase db)
    {
        await Exec(db, "INSERT INTO streams (name, created_utc) VALUES ('Stream A','t'), ('Stream B','t');");
        long a = await db.Repository.AppendAsync(Evt("routine heartbeat ok", "core-sw-a"), CancellationToken.None);
        long b = await db.Repository.AppendAsync(Evt("secret payload here", "core-sw-b"), CancellationToken.None);
        long streamA = await StreamIdAsync(db, "Stream A");
        long streamB = await StreamIdAsync(db, "Stream B");
        await Exec(db, $"INSERT INTO event_streams (event_id, stream_id) VALUES ({a},{streamA}), ({b},{streamB});");
        await db.SyncSearchAsync();
        return (a, b);
    }

    private static SyslogEvent Evt(string message, string host) => new()
    {
        ReceivedUtc = Base,
        SourceIp = "10.0.0.1",
        Hostname = host,
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc3164,
    };

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> StreamIdAsync(SqliteTestDatabase db, string name)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT stream_id FROM streams WHERE name = '{name}';";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
