using FluentAssertions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>
/// PHASE_10 "Tests to write first — Full lifecycle test": seed → age → Hot→Warm→Cold →
/// verify searchability at each tier → archive → verify hash → restore → confirm identical
/// → expire restore. One continuous scenario, checked at every stage.
/// </summary>
public sealed class RetentionFullLifecycleTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;
    private DateTimeOffset _origin;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task FullLifecycle_HotToWarmToColdToArchiveToRestoreToExpiry_EveryStageVerified()
    {
        _origin = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 10, DefaultWarmDays = 20, DefaultColdDays = 30 }, "operator", CancellationToken.None);

        var original = RetentionFixture.Event(_origin, "lifecycle-marker configuration changed");
        IReadOnlyList<long> ids = await _h.SeedEventsAsync([original]);
        long eventId = ids[0];

        // ---- Stage 1: Hot — searchable, uncompressed.
        (await Search()).Should().ContainSingle(e => e.EventId == eventId);
        (await TierOf(eventId)).Should().Be("hot");

        // ---- Stage 2: age past the Hot threshold (10 days) — advance the clock, not the data.
        _h.Clock.Advance(TimeSpan.FromDays(11));
        (await _h.Engine.TierToWarmBatchAsync(CancellationToken.None)).Should().Be(1);
        (await TierOf(eventId)).Should().Be("warm");

        // Stage 2b: still searchable, and content is still byte-identical (the "Warm-tier
        // search test").
        IReadOnlyList<SyslogEvent> warmResults = await Search();
        SyslogEvent? warmRead = warmResults.SingleOrDefault(e => e.EventId == eventId);
        warmRead.Should().NotBeNull();
        warmRead!.Message.Should().Be(original.Message);
        warmRead.RawMessage.ToArray().Should().Equal(original.RawMessage.ToArray());

        // ---- Stage 3: age past Hot+Warm (30 days total) — export to Cold.
        _h.Clock.Advance(TimeSpan.FromDays(20)); // total age now 31 days
        int exported = await _h.Engine.ExportColdBatchAsync(CancellationToken.None);
        exported.Should().Be(1);

        // The row has left the live database — no longer searchable there.
        (await Search()).Should().BeEmpty();

        ArchiveRecord archive = (await _h.Archives.ListAsync(null, CancellationToken.None)).Single();
        archive.EventCount.Should().Be(1);

        // ---- Stage 4: verify the archive's hash.
        byte[] onDisk = await File.ReadAllBytesAsync(archive.FilePath);
        VSoftSol.Syslog.Data.Retention.ArchiveFileStore.ComputeSha256Hex(onDisk).Should().Be(archive.Sha256);
        var verifications = await _h.Verifier.VerifyDueBatchAsync(TimeSpan.Zero, 10, CancellationToken.None);
        verifications.Should().ContainSingle(v => v.ArchiveId == archive.ArchiveId && v.Status == ArchiveStatus.Ok);

        // ---- Stage 5: restore, and confirm the data is identical to the original.
        RestoreRecord restore = await _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(2), CancellationToken.None);
        restore.EventCount.Should().Be(1);

        IReadOnlyList<SyslogEvent> restored = await Search();
        restored.Should().ContainSingle();
        restored[0].EventId.Should().Be(eventId);
        restored[0].Message.Should().Be(original.Message);
        restored[0].RawMessage.ToArray().Should().Equal(original.RawMessage.ToArray());
        restored[0].Hostname.Should().Be(original.Hostname);
        restored[0].SourceIp.Should().Be(original.SourceIp);

        // ---- Stage 6: the restore auto-expires and the data leaves the live database again
        // (the archive itself is untouched — it can be restored again later).
        _h.Clock.Advance(TimeSpan.FromDays(3));
        int expired = await _h.Engine.ExpireRestoresBatchAsync(10, CancellationToken.None);
        expired.Should().Be(1);
        (await Search()).Should().BeEmpty();

        ArchiveRecord? stillThere = await _h.Archives.GetAsync(archive.ArchiveId, CancellationToken.None);
        stillThere!.Status.Should().Be(ArchiveStatus.Ok);
    }

    private async Task<IReadOnlyList<SyslogEvent>> Search()
    {
        var result = await _h.Scoped.SearchAsync(
            UserScope.Unrestricted,
            new SearchRequest { QueryText = "lifecycle-marker", FromUtc = _origin.AddDays(-1), ToUtc = _h.Clock.GetUtcNow().AddDays(1) },
            CancellationToken.None);
        return result.Rows;
    }

    private async Task<string> TierOf(long eventId)
    {
        await using var connection = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tier FROM events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId);
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
