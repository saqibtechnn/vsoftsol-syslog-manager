using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// PHASE_05 "Query plan assertions": for the common query shapes, <c>EXPLAIN QUERY PLAN</c>
/// must use an index and must not degrade to a full scan of <c>events</c>. "A query that
/// silently degrades to a scan will pass a correctness test and fail a customer."
/// </summary>
public sealed class SearchQueryPlanTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset From = new(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string?> CommonShapes() => new()
    {
        { "", null },                              // time range only
        { "failed", "events_fts" },                // free text → FTS
        { "\"login failed\"", "events_fts" },      // phrase → FTS
        { "failed AND denied", "events_fts" },     // boolean text → FTS
        { "severity:error", null },                // severity + time
        { "host:core-sw-1", null },                // host equality
        { "source_ip:10.0.0.1", null },            // ip equality
        { "device_id:5", null },                   // device id
        { "field.user:root", null },               // custom field EXISTS
        { "failed AND severity:error", "events_fts" }, // text + filter
    };

    [Theory]
    [MemberData(nameof(CommonShapes))]
    public async Task QueryPlan_ForCommonShape_UsesAnIndexAndDoesNotFullScanEvents(string query, string? mustMention)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await SeedAsync(db);

        SearchParseResult parsed = SearchQueryParser.Parse(query);
        parsed.Success.Should().BeTrue(because: parsed.Error);

        var request = new SearchRequest { QueryText = query, FromUtc = From, ToUtc = To, Limit = 1000 };
        CompiledSearch compiled = new SearchCompiler().Compile(request, parsed.Query!, UserScope.Unrestricted);

        string sql =
            $"EXPLAIN QUERY PLAN SELECT e.event_id FROM events e WHERE {compiled.WhereSql} " +
            $"ORDER BY {compiled.OrderBySql} LIMIT 1000;";

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (SearchParameter p in compiled.Parameters)
        {
            command.Parameters.AddWithValue(p.Name, p.Value);
        }

        var plan = new StringBuilder();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(reader.GetOrdinal("detail")));
            }
        }

        string planText = plan.ToString();
        output.WriteLine($"query «{query}»\n{planText}");

        // No un-indexed scan of the base events table.
        planText.Should().NotContain("SCAN events e");
        planText.Should().MatchRegex(@"(SEARCH events e USING|SEARCH e USING)");

        if (mustMention is not null)
        {
            planText.Should().Contain(mustMention);
        }
    }

    private static async Task SeedAsync(SqliteTestDatabase db)
    {
        await using (SqliteConnection seed = await db.Factory.OpenAsync(CancellationToken.None))
        {
            await using SqliteCommand seedCmd = seed.CreateCommand();
            seedCmd.CommandText =
                "INSERT INTO devices (name, created_utc) VALUES " +
                "('d1','t'),('d2','t'),('d3','t'),('d4','t'),('d5','t');";
            await seedCmd.ExecuteNonQueryAsync();
        }

        var events = new List<SyslogEvent>(3000);
        for (int i = 0; i < 3000; i++)
        {
            events.Add(new SyslogEvent
            {
                ReceivedUtc = From.AddMinutes(i),
                SourceIp = i % 3 == 0 ? "10.0.0.1" : "10.0.0.2",
                Hostname = i % 2 == 0 ? "core-sw-1" : "edge-fw-1",
                AppName = "sshd",
                Facility = Facility.SecurityAuth,
                Severity = (Severity)(i % 8),
                Protocol = Protocol.Udp,
                Message = i % 2 == 0 ? "failed password denied" : "accepted publickey",
                RawMessage = Encoding.UTF8.GetBytes("m"),
                ParseStatus = ParseStatus.Rfc3164,
                DeviceId = i % 5 == 0 ? 5 : null,
                Fields = [new EventField("user", i % 4 == 0 ? "root" : "guest")],
            });
        }

        await db.Repository.AppendBatchAsync(events, CancellationToken.None);
        await db.SyncSearchAsync();

        // ANALYZE so the planner has real statistics, matching a populated production DB.
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "ANALYZE;";
        await cmd.ExecuteNonQueryAsync();
    }
}
