using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

public sealed class DiskSpillQueueTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsoftsol-spill-" + Guid.NewGuid().ToString("N"));

    private DiskSpillQueue New(Action<IngestionOptions>? configure = null)
    {
        var options = new IngestionOptions { SpillDirectory = _dir };
        configure?.Invoke(options);
        return new DiskSpillQueue(Options.Create(options), NullLogger<DiskSpillQueue>.Instance);
    }

    private static RawFrame Frame(int i, string ip = "10.0.0.1") => new(
        DateTimeOffset.UtcNow, ip, "udp:test", Protocol.Udp, Encoding.UTF8.GetBytes($"<13>frame-{i}"), truncated: false);

    [Fact]
    public async Task Enqueue_ThenLeaseAndCommit_ReturnsEveryFrameOnceAndEmptiesTheQueue()
    {
        await using DiskSpillQueue queue = New();
        await queue.RecoverAsync(default);

        for (int i = 0; i < 500; i++)
        {
            (await queue.EnqueueAsync(Frame(i), default)).Should().BeTrue();
        }

        await queue.ForceFlushAsync(default);
        queue.PendingFrameCount.Should().Be(500);

        var seen = new List<string>();
        DiskSpillQueue.SpillLease? lease;
        while ((lease = await queue.LeaseAsync(128, default)) is not null)
        {
            seen.AddRange(lease.Frames.Select(f => Encoding.UTF8.GetString(f.Payload.Span)));
            await lease.CommitAsync(default);
        }

        seen.Should().HaveCount(500);
        seen.Should().OnlyHaveUniqueItems();
        queue.PendingFrameCount.Should().Be(0);
        queue.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Enqueue_PastTheSegmentSize_RollsToNewSegments_AndDrainsAcrossThem()
    {
        await using DiskSpillQueue queue = New(o => o.SpillSegmentBytes = 4 * 1024);
        await queue.RecoverAsync(default);

        for (int i = 0; i < 2_000; i++)
        {
            await queue.EnqueueAsync(Frame(i), default);
        }

        await queue.ForceFlushAsync(default);
        Directory.EnumerateFiles(_dir, "seg-*").Count().Should().BeGreaterThan(1);

        int drained = 0;
        DiskSpillQueue.SpillLease? lease;
        while ((lease = await queue.LeaseAsync(256, default)) is not null)
        {
            drained += lease.Frames.Count;
            await lease.CommitAsync(default);
        }

        drained.Should().Be(2_000);
        // Consumed segments are deleted.
        Directory.EnumerateFiles(_dir, "seg-*").Count().Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task RecoverAsync_AfterAnUncleanShutdownWithATornTail_KeepsEveryWholeRecord()
    {
        await using (DiskSpillQueue writer = New())
        {
            await writer.RecoverAsync(default);
            for (int i = 0; i < 100; i++)
            {
                await writer.EnqueueAsync(Frame(i), default);
            }

            await writer.ForceFlushAsync(default);
        }

        // Simulate a torn write: append 5 junk bytes to the newest segment.
        string seg = Directory.EnumerateFiles(_dir, "seg-*").OrderBy(f => f).Last();
        await using (var fs = new FileStream(seg, FileMode.Append))
        {
            await fs.WriteAsync(new byte[] { 9, 9, 9, 9, 9 });
        }

        await using DiskSpillQueue recovered = New();
        long carried = await recovered.RecoverAsync(default);

        carried.Should().Be(100);

        int drained = 0;
        DiskSpillQueue.SpillLease? lease;
        while ((lease = await recovered.LeaseAsync(1000, default)) is not null)
        {
            drained += lease.Frames.Count;
            await lease.CommitAsync(default);
        }

        drained.Should().Be(100);
    }

    [Fact]
    public async Task LeasedButNotCommitted_IsRedeliveredOnTheNextLease_NeverLost()
    {
        await using DiskSpillQueue queue = New();
        await queue.RecoverAsync(default);
        for (int i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync(Frame(i), default);
        }

        await queue.ForceFlushAsync(default);

        DiskSpillQueue.SpillLease? first = await queue.LeaseAsync(10, default);
        first!.Frames.Should().HaveCount(10);
        // abandon it (no CommitAsync) — as if the process crashed mid-commit

        DiskSpillQueue.SpillLease? again = await queue.LeaseAsync(10, default);
        again!.Frames.Should().HaveCount(10, "an uncommitted lease is redelivered — at-least-once, never lost");
    }

    [Fact]
    public async Task EnqueueAsync_AtTheHardSizeCap_ReturnsFalse_WithoutTouchingFramesAlreadyOnDisk()
    {
        await using DiskSpillQueue queue = New(o =>
        {
            o.SpillMaxBytes = 16 * 1024 * 1024; // floor of the option range
            o.SpillSegmentBytes = 1 * 1024 * 1024;
        });
        await queue.RecoverAsync(default);

        int accepted = 0;
        int rejected = 0;
        byte[] big = new byte[60_000];
        for (int i = 0; i < 1_000; i++)
        {
            var frame = new RawFrame(DateTimeOffset.UtcNow, "10.0.0.9", "udp:test", Protocol.Udp, big, false);
            if (await queue.EnqueueAsync(frame, default))
            {
                accepted++;
            }
            else
            {
                rejected++;
            }
        }

        rejected.Should().BeGreaterThan(0, "the cap must bite");
        await queue.ForceFlushAsync(default);

        int drained = 0;
        DiskSpillQueue.SpillLease? lease;
        while ((lease = await queue.LeaseAsync(256, default)) is not null)
        {
            drained += lease.Frames.Count;
            await lease.CommitAsync(default);
        }

        drained.Should().Be(accepted, "every accepted frame survives even when the cap rejected others");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        await Task.CompletedTask;
    }
}
