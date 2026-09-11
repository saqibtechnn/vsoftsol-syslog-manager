using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 — the tamper-detection matrix: bit-flip, truncation, and swapping in an
/// archive from a different period must all be detected (TESTING_STANDARDS.md Validation &amp;
/// Evidence: "3/3 detected").</summary>
public sealed class ArchiveTamperMatrixTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static SyslogEvent Event(DateTimeOffset receivedUtc, string message) => new()
    {
        ReceivedUtc = receivedUtc,
        SourceIp = "10.0.0.1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc5424,
    };

    private async Task<ArchiveRecord> ExportOnePeriodAsync(DateTimeOffset receivedUtc, string message)
    {
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        await _h.SeedEventsAsync([Event(receivedUtc, message)]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);
        return (await _h.Archives.ListAsync(null, CancellationToken.None)).OrderByDescending(a => a.ArchiveId).First();
    }

    [Fact]
    public async Task VerifyDueBatchAsync_DetectsAllThreeTamperKinds()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();

        ArchiveRecord bitFlipTarget = await ExportOnePeriodAsync(now.AddDays(-30), "bitflip-target");
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        ArchiveRecord truncateTarget = await ExportOnePeriodAsync(now.AddDays(-40), "truncate-target");
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        ArchiveRecord swapVictim = await ExportOnePeriodAsync(now.AddDays(-50), "swap-victim");
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        ArchiveRecord swapDonor = await ExportOnePeriodAsync(now.AddDays(-60), "swap-donor-from-a-different-period");

        // 1. Bit flip.
        byte[] bytes = await File.ReadAllBytesAsync(bitFlipTarget.FilePath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(bitFlipTarget.FilePath, bytes);

        // 2. Truncation.
        byte[] full = await File.ReadAllBytesAsync(truncateTarget.FilePath);
        await File.WriteAllBytesAsync(truncateTarget.FilePath, full[..(full.Length / 2)]);

        // 3. Swap with a valid archive from a different period (same byte-validity, wrong hash).
        byte[] donorContent = await File.ReadAllBytesAsync(swapDonor.FilePath);
        await File.WriteAllBytesAsync(swapVictim.FilePath, donorContent);

        var results = await _h.Verifier.VerifyDueBatchAsync(TimeSpan.Zero, 100, CancellationToken.None);

        results.Where(r => r.ArchiveId == bitFlipTarget.ArchiveId).Single().Status.Should().Be(ArchiveStatus.TamperDetected);
        results.Where(r => r.ArchiveId == truncateTarget.ArchiveId).Single().Status.Should().Be(ArchiveStatus.TamperDetected);
        results.Where(r => r.ArchiveId == swapVictim.ArchiveId).Single().Status.Should().Be(ArchiveStatus.TamperDetected);
        results.Where(r => r.ArchiveId == swapDonor.ArchiveId).Single().Status.Should().Be(ArchiveStatus.Ok);

        (await _h.Archives.GetAsync(bitFlipTarget.ArchiveId, CancellationToken.None))!.Status.Should().Be(ArchiveStatus.TamperDetected);
    }

    [Fact]
    public async Task VerifyDueBatchAsync_ADeletedFile_IsReportedMissing_NotSilentlySkipped()
    {
        ArchiveRecord archive = await ExportOnePeriodAsync(_h.Clock.GetUtcNow().AddDays(-30), "will-vanish");
        File.Delete(archive.FilePath);

        var results = await _h.Verifier.VerifyDueBatchAsync(TimeSpan.Zero, 100, CancellationToken.None);

        results.Should().ContainSingle(r => r.ArchiveId == archive.ArchiveId && r.Status == ArchiveStatus.Missing);
    }

    [Fact]
    public async Task VerifyDueBatchAsync_ARecentlyVerifiedArchive_IsNotReVerifiedBeforeItGoesStale()
    {
        ArchiveRecord archive = await ExportOnePeriodAsync(_h.Clock.GetUtcNow().AddDays(-30), "fresh");
        await _h.Verifier.VerifyDueBatchAsync(TimeSpan.FromDays(7), 100, CancellationToken.None);

        var results = await _h.Verifier.VerifyDueBatchAsync(TimeSpan.FromDays(7), 100, CancellationToken.None);

        results.Should().BeEmpty();
        _ = archive;
    }
}
