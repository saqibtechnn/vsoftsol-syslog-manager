using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class SqliteLogRepositoryTests
{
    [Fact]
    public async Task AppendAsync_ThenGetById_RoundTripsEveryField()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SyslogEvent original = SampleEvents.Full();

        long id = await db.Repository.AppendAsync(original, CancellationToken.None);
        SyslogEvent? loaded = await db.Repository.GetByIdAsync(id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.EventId.Should().Be(id);
        loaded.ReceivedUtc.Should().Be(original.ReceivedUtc);
        loaded.EventUtc.Should().Be(original.EventUtc);
        loaded.SourceIp.Should().Be(original.SourceIp);
        loaded.Hostname.Should().Be(original.Hostname);
        loaded.AppName.Should().Be(original.AppName);
        loaded.ProcId.Should().Be(original.ProcId);
        loaded.MsgId.Should().Be(original.MsgId);
        loaded.Facility.Should().Be(original.Facility);
        loaded.Severity.Should().Be(original.Severity);
        loaded.Protocol.Should().Be(original.Protocol);
        loaded.Message.Should().Be(original.Message);
        loaded.RawMessage.ToArray().Should().Equal(original.RawMessage.ToArray());
        loaded.ParseStatus.Should().Be(original.ParseStatus);
        loaded.OccurrenceCount.Should().Be(original.OccurrenceCount);
        loaded.StructuredDataJson.Should().Be(original.StructuredDataJson);
        loaded.Vendor.Should().Be(original.Vendor);
        loaded.Fields.Should().BeEquivalentTo(original.Fields);
    }

    [Fact]
    public async Task GetByIdAsync_ForUnknownId_ReturnsNull()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await db.Repository.GetByIdAsync(999_999, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AppendBatchAsync_ReturnsIdsInInputOrder_AndPersistsAll()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync(o => o.InsertBatchSize = 7);
        var batch = Enumerable.Range(0, 50).Select(i => SampleEvents.Minimal($"batch {i:D2}")).ToList();

        IReadOnlyList<long> ids = await db.Repository.AppendBatchAsync(batch, CancellationToken.None);

        ids.Should().HaveCount(50);
        ids.Should().OnlyHaveUniqueItems();
        for (int i = 0; i < batch.Count; i++)
        {
            SyslogEvent? loaded = await db.Repository.GetByIdAsync(ids[i], CancellationToken.None);
            loaded!.Message.Should().Be($"batch {i:D2}");
        }

        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().Be(50);
    }

    [Fact]
    public async Task QueryAsync_FiltersByTimeRangeSeverityAndText()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await db.Repository.AppendBatchAsync(
        [
            Sev(baseTime.AddMinutes(1), Severity.Informational, "login ok for alice"),
            Sev(baseTime.AddMinutes(2), Severity.Error, "login failed for bob"),
            Sev(baseTime.AddMinutes(3), Severity.Error, "disk failure imminent"),
            Sev(baseTime.AddHours(5), Severity.Error, "login failed for carol"),
        ], CancellationToken.None);
        await db.SyncSearchAsync();

        var query = new LogQuery
        {
            FromUtc = baseTime,
            ToUtc = baseTime.AddHours(1),
            Severities = [Severity.Error],
            FullText = "failed",
        };

        List<SyslogEvent> hits = [];
        await foreach (SyslogEvent e in db.Repository.QueryAsync(query, CancellationToken.None))
        {
            hits.Add(e);
        }

        hits.Should().ContainSingle().Which.Message.Should().Be("login failed for bob");
        (await db.Repository.CountAsync(query, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task QueryAsync_HonoursOrderingAndPaging()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var t0 = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
        await db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 10).Select(i => SampleEvents.Minimal($"m{i}", receivedUtc: t0.AddSeconds(i))).ToList(),
            CancellationToken.None);

        var page = new List<string>();
        await foreach (SyslogEvent e in db.Repository.QueryAsync(
            new LogQuery { Descending = false, Limit = 3, Offset = 2 }, CancellationToken.None))
        {
            page.Add(e.Message);
        }

        page.Should().Equal("m2", "m3", "m4");
    }

    [Fact]
    public async Task GetContextAsync_ReturnsSurroundingEventsFromSameSourceOldestFirst()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var t0 = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);

        var ids = await db.Repository.AppendBatchAsync(
        [
            SampleEvents.Minimal("a", "10.0.0.1", t0.AddSeconds(0)),
            SampleEvents.Minimal("noise", "10.0.0.2", t0.AddSeconds(1)),
            SampleEvents.Minimal("b", "10.0.0.1", t0.AddSeconds(2)),
            SampleEvents.Minimal("c-anchor", "10.0.0.1", t0.AddSeconds(3)),
            SampleEvents.Minimal("d", "10.0.0.1", t0.AddSeconds(4)),
            SampleEvents.Minimal("e", "10.0.0.1", t0.AddSeconds(5)),
        ], CancellationToken.None);

        IReadOnlyList<SyslogEvent> context =
            await db.Repository.GetContextAsync(ids[3], before: 1, after: 1, CancellationToken.None);

        context.Select(e => e.Message).Should().Equal("b", "c-anchor", "d");
    }

    private static SyslogEvent Sev(DateTimeOffset when, Severity severity, string message) => new()
    {
        ReceivedUtc = when,
        SourceIp = "203.0.113.5",
        Facility = Facility.Local0,
        Severity = severity,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Raw,
    };
}
