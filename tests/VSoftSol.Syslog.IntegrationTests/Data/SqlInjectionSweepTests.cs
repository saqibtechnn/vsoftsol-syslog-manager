using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>
/// SECURITY_STANDARDS.md §5.1 / PHASE_01: every repository path binds parameters; hostile
/// strings in every position are stored and matched literally, never executed.
/// </summary>
public sealed class SqlInjectionSweepTests
{
    private static readonly string[] Cwe89Corpus =
    [
        "'; DROP TABLE events; --",
        "\" OR \"1\"=\"1",
        "1); DELETE FROM events; --",
        "admin'--",
        "' UNION SELECT password_hash FROM users --",
        "Robert'); DROP TABLE students;--",
        "%27%20OR%201=1",
        "'||(SELECT sqlite_version())||'",
    ];

    [Fact]
    public async Task HostilePayloads_InEveryTextField_AreStoredVerbatim_AndDoNotExecute()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        foreach (string payload in Cwe89Corpus)
        {
            var evt = new SyslogEvent
            {
                ReceivedUtc = DateTimeOffset.UtcNow,
                SourceIp = payload,
                Hostname = payload,
                AppName = payload,
                Message = payload,
                RawMessage = System.Text.Encoding.UTF8.GetBytes(payload),
                Facility = Core.Enums.Facility.Local0,
                Severity = Core.Enums.Severity.Informational,
                Protocol = Core.Enums.Protocol.Udp,
                ParseStatus = Core.Enums.ParseStatus.Raw,
                Fields = [new EventField(payload, payload)],
            };

            long id = await db.Repository.AppendAsync(evt, CancellationToken.None);
            SyslogEvent? loaded = await db.Repository.GetByIdAsync(id, CancellationToken.None);
            loaded!.Message.Should().Be(payload);
            loaded.Hostname.Should().Be(payload);
        }

        // The schema and data survived every payload.
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='events';";
        Convert.ToInt64(await tables.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(1);
        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().Be(Cwe89Corpus.Length);
    }

    [Fact]
    public async Task QueryFilters_WithHostileValues_ReturnNoRows_AndDoNotError()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Repository.AppendAsync(SampleEvents.Minimal("normal"), CancellationToken.None);
        await db.SyncSearchAsync();

        foreach (string payload in Cwe89Corpus)
        {
            var query = new LogQuery { FullText = payload };
            long count = await db.Repository.CountAsync(query, CancellationToken.None);
            count.Should().Be(0);
        }

        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public void ToFtsPhrase_EscapesEmbeddedQuotes()
    {
        SqliteLogRepository.ToFtsPhrase("a \" b").Should().Be("\"a \"\" b\"");
    }
}
