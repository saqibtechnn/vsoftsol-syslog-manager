using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 — the tiering engine: Hot→Warm compression, Warm→Cold export, primary-stream
/// policy resolution, and checkpoint resumability.</summary>
public sealed class SqliteRetentionEngineTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static SyslogEvent Event(DateTimeOffset receivedUtc, string message, IReadOnlyList<long>? streamIds = null) => new()
    {
        ReceivedUtc = receivedUtc,
        SourceIp = "10.0.0.1",
        Hostname = "host-1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc5424,
        StreamIds = streamIds ?? [],
    };

    [Fact]
    public async Task TierToWarmBatchAsync_CompressesOnlyEventsPastTheHotThreshold()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 10, DefaultWarmDays = 90, DefaultColdDays = 365 }, "test", CancellationToken.None);

        IReadOnlyList<long> ids = await _h.SeedEventsAsync(
        [
            Event(now.AddDays(-20), "old enough for warm"),
            Event(now.AddDays(-1), "too young for warm"),
        ]);

        int moved = await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);

        moved.Should().Be(1);
        (await TierOf(ids[0])).Should().Be("warm");
        (await TierOf(ids[1])).Should().Be("hot");
    }

    [Fact]
    public async Task TierToWarmBatchAsync_WarmRowsRemainByteIdenticalOnRead_TheWarmTierSearchTest()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1 }, "test", CancellationToken.None);
        byte[] rawBytes = [0, 1, 2, 0xFF, (byte)'\n', (byte)'"', (byte)'\\'];
        var evt = new SyslogEvent
        {
            ReceivedUtc = now.AddDays(-5),
            SourceIp = "10.0.0.1",
            Facility = Facility.Local0,
            Severity = Severity.Warning,
            Protocol = Protocol.Udp,
            Message = "a message with unicode café and \"quotes\"",
            RawMessage = rawBytes,
            ParseStatus = ParseStatus.Rfc5424,
        };
        IReadOnlyList<long> ids = await _h.SeedEventsAsync([evt]);

        (await _h.Engine.TierToWarmBatchAsync(CancellationToken.None)).Should().Be(1);

        SyslogEvent? readBack = await _h.Scoped.GetByIdAsync(
            VSoftSol.Syslog.Core.Security.UserScope.Unrestricted, ids[0], CancellationToken.None);

        readBack.Should().NotBeNull();
        readBack!.Message.Should().Be(evt.Message);
        readBack.RawMessage.ToArray().Should().Equal(rawBytes);

        // Search must still find it via FTS (untouched by warm compression).
        var searchResult = await _h.Scoped.SearchAsync(
            VSoftSol.Syslog.Core.Security.UserScope.Unrestricted,
            new VSoftSol.Syslog.Data.Search.SearchRequest { QueryText = "unicode", FromUtc = now.AddDays(-10), ToUtc = now },
            CancellationToken.None);
        searchResult.Rows.Should().ContainSingle(r => r.EventId == ids[0]);
    }

    [Fact]
    public async Task TierToWarmBatchAsync_UsesThePrimaryStreamPolicy_NotTheCatchAll()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1 }, "test", CancellationToken.None); // catch-all default: 1 day
        long catchAll = await _h.CreateStreamAsync("All Messages", isCatchAll: true);
        long specific = await _h.CreateStreamAsync("Firewall");
        await _h.Policies.SavePolicyAsync(new RetentionPolicy { StreamId = specific, HotDays = 365, WarmDays = 90, ColdDays = 365 }, "test", CancellationToken.None);

        // 10 days old: past the catch-all's 1-day default, but far short of the specific
        // stream's 365-day override — the primary (non-catch-all) stream must win.
        IReadOnlyList<long> ids = await _h.SeedEventsAsync([Event(now.AddDays(-10), "m", [catchAll, specific])]);

        (await _h.Engine.TierToWarmBatchAsync(CancellationToken.None)).Should().Be(0);
        (await TierOf(ids[0])).Should().Be("hot");
    }

    [Fact]
    public async Task TierToWarmBatchAsync_IsResumable_ViaCheckpoint()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, BatchSize = 2 }, "test", CancellationToken.None);
        await _h.SeedEventsAsync([
            Event(now.AddDays(-10), "a"), Event(now.AddDays(-9), "b"), Event(now.AddDays(-8), "c"),
        ]);

        int first = await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        int second = await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        int third = await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);

        first.Should().Be(2);
        second.Should().Be(1);
        third.Should().Be(0);
    }

    [Fact]
    public async Task ExportColdBatchAsync_ArchivesAndRemovesEligibleWarmEvents()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1, DefaultColdDays = 365 }, "test", CancellationToken.None);
        IReadOnlyList<long> ids = await _h.SeedEventsAsync([
            Event(now.AddDays(-10), "one"), Event(now.AddDays(-9), "two"),
        ]);

        (await _h.Engine.TierToWarmBatchAsync(CancellationToken.None)).Should().Be(2);
        int exported = await _h.Engine.ExportColdBatchAsync(CancellationToken.None);

        exported.Should().Be(2);
        (await EventExists(ids[0])).Should().BeFalse();
        (await EventExists(ids[1])).Should().BeFalse();

        IReadOnlyList<ArchiveRecord> archives = await _h.Archives.ListAsync(null, CancellationToken.None);
        archives.Should().ContainSingle();
        archives[0].EventCount.Should().Be(2);
        File.Exists(archives[0].FilePath).Should().BeTrue();

        byte[] onDisk = await File.ReadAllBytesAsync(archives[0].FilePath);
        VSoftSol.Syslog.Data.Retention.ArchiveFileStore.ComputeSha256Hex(onDisk).Should().Be(archives[0].Sha256);
    }

    [Fact]
    public async Task ExportColdBatchAsync_GroupsByPrimaryStream_IntoSeparateArchives()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        long streamA = await _h.CreateStreamAsync("Stream A");
        long streamB = await _h.CreateStreamAsync("Stream B");
        await _h.SeedEventsAsync([
            Event(now.AddDays(-10), "a", [streamA]),
            Event(now.AddDays(-10), "b", [streamB]),
        ]);

        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);

        IReadOnlyList<ArchiveRecord> archives = await _h.Archives.ListAsync(null, CancellationToken.None);
        archives.Should().HaveCount(2);
        archives.Select(a => a.StreamName).Should().BeEquivalentTo(["Stream A", "Stream B"]);
    }

    [Fact]
    public async Task Retention_ResumesCleanlyAndWithoutDuplicates_AfterASimulatedCrashBetweenArchiveWriteAndEventDelete()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        IReadOnlyList<long> ids = await _h.SeedEventsAsync([Event(now.AddDays(-10), "one")]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);

        // Simulate "the archive file+row committed, but the process died before the delete
        // transaction ran": pre-create the exact archive row the real export would produce.
        string streamName = "unstreamed";
        string fileName = VSoftSol.Syslog.Core.Retention.ArchiveNaming.BuildFileName(streamName, now.AddDays(-10), now.AddDays(-10));
        string archivesDir = Path.Combine(Path.GetDirectoryName(_h.Db.DatabasePath)!, "archives");
        Directory.CreateDirectory(archivesDir);
        string fullPath = Path.Combine(archivesDir, fileName);
        await File.WriteAllBytesAsync(fullPath, "placeholder-content"u8.ToArray());
        long preArchiveId = await _h.Archives.CreateOrReplaceAsync(new ArchiveRecord
        {
            StreamName = streamName,
            FilePath = fullPath,
            PeriodStartUtc = now.AddDays(-10),
            PeriodEndUtc = now.AddDays(-10),
            EventCount = 1,
            ByteSize = 19,
            Sha256 = "deadbeef",
        }, CancellationToken.None);

        // The event was never deleted (simulating the crash) — it is still there to retry.
        (await EventExists(ids[0])).Should().BeTrue();

        int exported = await _h.Engine.ExportColdBatchAsync(CancellationToken.None);

        exported.Should().Be(1);
        (await EventExists(ids[0])).Should().BeFalse("the retry must finish the delete");
        IReadOnlyList<ArchiveRecord> archives = await _h.Archives.ListAsync(null, CancellationToken.None);
        archives.Should().ContainSingle("the retry must not create a second archive row for the same file");
        archives[0].ArchiveId.Should().Be(preArchiveId);
        archives[0].EventCount.Should().Be(1, "the real content overwrites the placeholder");
    }

    [Fact]
    public async Task PurgeExpiredArchivesBatchAsync_DeletesTheFile_AndMarksTheRowDeleted()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1, DefaultColdDays = 5 }, "test", CancellationToken.None);
        await _h.SeedEventsAsync([Event(now.AddDays(-20), "old")]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);
        ArchiveRecord archive = (await _h.Archives.ListAsync(null, CancellationToken.None)).Single();

        // Advance the clock so the archive (created "now", i.e. 20 days after the event but
        // the archive's own created_utc is "now") passes the 5-day cold horizon.
        _h.Clock.Advance(TimeSpan.FromDays(6));

        int purged = await _h.Engine.PurgeExpiredArchivesBatchAsync(100, CancellationToken.None);

        purged.Should().Be(1);
        File.Exists(archive.FilePath).Should().BeFalse();
        ArchiveRecord? after = await _h.Archives.GetAsync(archive.ArchiveId, CancellationToken.None);
        after!.Status.Should().Be(ArchiveStatus.Deleted);
    }

    private async Task<string> TierOf(long eventId)
    {
        await using var connection = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tier FROM events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId);
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private async Task<bool> EventExists(long eventId)
    {
        await using var connection = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId);
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))! == 1;
    }
}
