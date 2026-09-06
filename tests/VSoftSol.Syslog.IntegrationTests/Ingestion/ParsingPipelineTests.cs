using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>
/// End-to-end: frames through <see cref="IngestionPipeline"/> are parsed by
/// <c>MessageParser</c> and land in SQLite with their structured fields, vendor, and
/// (for failures) a link to the "Parse Failures" stream. Also covers the dedup window.
/// </summary>
[Trait("Category", "Ingestion")]
[Trait("Category", "Parsing")]
public sealed class ParsingPipelineTests
{
    private static RawFrame Frame(string wire, string ip = "203.0.113.7") => new(
        DateTimeOffset.UtcNow, ip, "udp:test", Protocol.Udp, System.Text.Encoding.UTF8.GetBytes(wire), truncated: false);

    [Fact]
    public async Task ParsedCiscoMessage_LandsWithStructuredFieldsAndVendor()
    {
        await using IngestionHarness h = await IngestionHarness.CreateAsync();
        h.StartPipeline();

        await h.Intake.AcceptAsync(Frame(
            "<190>123456: Mar  1 22:14:15.003 UTC: %LINK-3-UPDOWN: Interface GigabitEthernet0/1, changed state to down"), default);
        await h.DrainAsync();

        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.ParseStatus.Should().Be(ParseStatus.Rfc3164);
        e.Vendor.Should().Be("cisco-ios");
        e.Message.Should().Contain("%LINK-3-UPDOWN");
        e.Fields.Should().Contain(f => f.Name == "cisco_mnemonic" && f.Value == "UPDOWN");
        e.Fields.Should().Contain(f => f.Name == "interface" && f.Value == "GigabitEthernet0/1");
    }

    [Fact]
    public async Task UnparseableMessage_LandsAsRaw_AndIsLinkedToTheParseFailuresStream()
    {
        await using IngestionHarness h = await IngestionHarness.CreateAsync();
        h.StartPipeline();

        await h.Intake.AcceptAsync(Frame("just some free text from an unknown appliance @@@"), default);
        await h.DrainAsync();

        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.ParseStatus.Should().Be(ParseStatus.Raw);

        await using SqliteConnection conn = await h.Db.Factory.OpenAsync(default);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.name FROM event_streams es
            JOIN streams s ON s.stream_id = es.stream_id
            WHERE es.event_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", e.EventId);
        (await cmd.ExecuteScalarAsync())!.ToString().Should().Be("Parse Failures");
    }

    [Fact]
    public async Task WireObservedSourceIp_IsAuthoritative_OverAClaimedHostname()
    {
        await using IngestionHarness h = await IngestionHarness.CreateAsync();
        h.StartPipeline();

        await h.Intake.AcceptAsync(Frame(
            "<38>Oct 12 09:15:00 dc01.corp.local sshd[1234]: Accepted publickey for admin from 10.0.0.5 port 22 ssh2",
            ip: "192.0.2.240"), default);
        await h.DrainAsync();

        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.SourceIp.Should().Be("192.0.2.240");
        e.Hostname.Should().Be("dc01.corp.local");
        e.Vendor.Should().Be("linux");
    }

    [Fact]
    public async Task DeduplicationWindow_FoldsRepeatsIntoOccurrenceCount_NoDuplicateRow()
    {
        await using IngestionHarness h = await IngestionHarness.CreateAsync(
            configureParsing: p => p.DeduplicationWindow = TimeSpan.FromMinutes(5));
        h.StartPipeline();

        const string wire = "<190>44444: Mar  1 22:14:15.003 UTC: %LINEPROTO-5-UPDOWN: Line protocol on Interface Gi0/1, changed state to down";

        await h.Intake.AcceptAsync(Frame(wire), default);

        // Wait for the first message to commit (and register in the dedup window) before the repeats.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && await h.CommittedCountAsync() < 1)
        {
            await Task.Delay(20);
        }

        for (int i = 0; i < 5; i++)
        {
            await h.Intake.AcceptAsync(Frame(wire), default);
        }

        await h.DrainAsync(TimeSpan.FromSeconds(30));

        (await h.CommittedCountAsync()).Should().Be(1, "the repeats folded into one row");

        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.OccurrenceCount.Should().Be(6);
    }

    [Fact]
    public async Task DeduplicationDisabledByDefault_EveryMessageIsItsOwnRow()
    {
        await using IngestionHarness h = await IngestionHarness.CreateAsync();
        h.StartPipeline();

        for (int i = 0; i < 10; i++)
        {
            await h.Intake.AcceptAsync(Frame("<13>Oct 12 09:00:00 host app: identical"), default);
        }

        await h.DrainAsync();
        (await h.CommittedCountAsync()).Should().Be(10);
    }
}
