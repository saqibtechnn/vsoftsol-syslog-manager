using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class Fts5SearchTests
{
    [Fact]
    public async Task Query_FullText_FindsMessagesContainingTheToken()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        string[] messages =
        [
            "sshd accepted password for admin",
            "sshd failed password for root",
            "kernel: eth0 link is up",
            "kernel: eth0 link is down",
            "sudo session opened for user deploy",
            "cron job completed successfully",
            "dhcpd assigned 10.20.30.40 to aa:bb:cc:dd:ee:ff",
            "firewall denied 10.20.30.40 -> 8.8.8.8",
            "nginx 200 GET /health",
            "nginx 500 GET /api/orders",
        ];
        await db.Repository.AppendBatchAsync(messages.Select(m => SampleEvents.Minimal(m)).ToList(), CancellationToken.None);

        (await Search(db, "password")).Should().Be(2);
        (await Search(db, "eth0")).Should().Be(2);
        (await Search(db, "nonexistentxyz")).Should().Be(0);
    }

    [Fact]
    public async Task Query_FullText_TreatsIpAndMacAsSingleTokens()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Repository.AppendBatchAsync(
        [
            SampleEvents.Minimal("dhcpd assigned 10.20.30.40 to aa:bb:cc:dd:ee:ff"),
            SampleEvents.Minimal("dhcpd assigned 10.20.30.99 to 11:22:33:44:55:66"),
            SampleEvents.Minimal("no addresses here"),
        ], CancellationToken.None);

        (await Search(db, "10.20.30.40")).Should().Be(1);
        (await Search(db, "aa:bb:cc:dd:ee:ff")).Should().Be(1);
    }

    [Fact]
    public async Task Query_FullText_MatchesRawBytesForUnparsedMessages()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Repository.AppendAsync(new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "192.0.2.1",
            Facility = Core.Enums.Facility.Local0,
            Severity = Core.Enums.Severity.Notice,
            Protocol = Core.Enums.Protocol.Udp,
            Message = string.Empty,
            RawMessage = System.Text.Encoding.UTF8.GetBytes("<13>garbled UNRECOGNISEDVENDOR frame kw=value"),
            ParseStatus = Core.Enums.ParseStatus.Raw,
        }, CancellationToken.None);

        (await Search(db, "UNRECOGNISEDVENDOR")).Should().Be(1);
    }

    [Fact]
    public async Task Query_FullText_TreatsUserInputAsLiteralPhraseNotFtsOperators()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Repository.AppendBatchAsync(
        [
            SampleEvents.Minimal("alpha bravo charlie"),
            SampleEvents.Minimal("bravo only"),
        ], CancellationToken.None);

        // A raw FTS query of `alpha OR bravo` would match both rows; as a literal phrase it matches neither.
        (await Search(db, "alpha OR bravo")).Should().Be(0);
        (await Search(db, "alpha bravo charlie")).Should().Be(1);
    }

    private static async Task<int> Search(SqliteTestDatabase db, string text)
    {
        await db.SyncSearchAsync();

        int count = 0;
        await foreach (SyslogEvent _ in db.Repository.QueryAsync(new LogQuery { FullText = text }, CancellationToken.None))
        {
            count++;
        }

        return count;
    }
}
