using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Reporting.Export;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// PHASE_05 security validation for export: CSV formula injection neutralised in the export
/// only (byte-identical in the DB), export DoS (a 50M-row request stays streamed and
/// capped), and the export path honours scope.
/// </summary>
public sealed class SearchExportSecurityTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static SearchRequest Request(int limit) => new()
    {
        QueryText = string.Empty,
        FromUtc = Base.AddDays(-1),
        ToUtc = Base.AddDays(1),
        Limit = limit,
    };

    [Fact]
    public async Task Csv_FormulaInjection_IsNeutralisedInExport_ButByteIdenticalInTheDatabase()
    {
        const string payload = "=HYPERLINK(\"http://evil\",\"click\")";
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);

        long id = await db.Repository.AppendAsync(new SyslogEvent
        {
            ReceivedUtc = Base,
            SourceIp = "10.0.0.1",
            Hostname = "h1",
            Facility = Facility.Local0,
            Severity = Severity.Warning,
            Protocol = Protocol.Udp,
            Message = payload,
            RawMessage = Encoding.UTF8.GetBytes(payload),
            ParseStatus = ParseStatus.Rfc3164,
        }, CancellationToken.None);
        await db.SyncSearchAsync();

        await using var sw = new StringWriter();
        await SearchExportWriter.WriteAsync(
            ExportFormat.Csv, sw, reader.SearchStreamAsync(UserScope.Unrestricted, Request(100), CancellationToken.None),
            CancellationToken.None);
        string csv = sw.ToString();

        // Safe in the CSV: the message cell is prefixed so a spreadsheet renders it as text.
        csv.Should().Contain("'" + payload.Replace("\"", "\"\"", StringComparison.Ordinal));
        csv.Should().NotContain(",=HYPERLINK"); // never a bare formula at a cell boundary

        // Byte-identical in the database.
        SyslogEvent? stored = await db.Repository.GetByIdAsync(id, CancellationToken.None);
        stored!.Message.Should().Be(payload);
        Encoding.UTF8.GetString(stored.RawMessage.Span).Should().Be(payload);
    }

    [Fact]
    public async Task Export_RequestingFiftyMillionRows_StaysStreamedAndCapped()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory,
            Microsoft.Extensions.Options.Options.Create(new SearchOptions { ExportMaxRows = 500 }));

        var events = new List<SyslogEvent>(2000);
        for (int i = 0; i < 2000; i++)
        {
            events.Add(new SyslogEvent
            {
                ReceivedUtc = Base.AddSeconds(i),
                SourceIp = "10.0.0.1",
                Facility = Facility.Local0,
                Severity = Severity.Informational,
                Protocol = Protocol.Udp,
                Message = $"row {i}",
                RawMessage = Encoding.UTF8.GetBytes("r"),
                ParseStatus = ParseStatus.Rfc3164,
            });
        }

        await db.Repository.AppendBatchAsync(events, CancellationToken.None);
        await db.SyncSearchAsync();

        int streamed = 0;
        await foreach (SyslogEvent _ in reader.SearchStreamAsync(
            UserScope.Unrestricted, Request(50_000_000), CancellationToken.None))
        {
            streamed++;
        }

        streamed.Should().Be(500, "the export cap bounds the result regardless of what the caller asks for");
    }

    [Fact]
    public async Task Export_IsScoped_LikeTheGrid()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);

        await Exec(db, "INSERT INTO streams (name, created_utc) VALUES ('S-A','t'), ('S-B','t');");
        long a = await db.Repository.AppendAsync(Msg("in A"), CancellationToken.None);
        long b = await db.Repository.AppendAsync(Msg("in B"), CancellationToken.None);
        long sa = await Scalar(db, "SELECT stream_id FROM streams WHERE name='S-A';");
        long sb = await Scalar(db, "SELECT stream_id FROM streams WHERE name='S-B';");
        await Exec(db, $"INSERT INTO event_streams (event_id, stream_id) VALUES ({a},{sa}), ({b},{sb});");
        await db.SyncSearchAsync();

        UserScope scoped = UserScope.Create(streamIds: [sa], deviceGroupIds: null);
        var ids = new List<long>();
        await foreach (SyslogEvent e in reader.SearchStreamAsync(scoped, Request(100), CancellationToken.None))
        {
            ids.Add(e.EventId);
        }

        ids.Should().Contain(a).And.NotContain(b);
    }

    private static SyslogEvent Msg(string m) => new()
    {
        ReceivedUtc = Base,
        SourceIp = "10.0.0.1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = m,
        RawMessage = Encoding.UTF8.GetBytes(m),
        ParseStatus = ParseStatus.Rfc3164,
    };

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
