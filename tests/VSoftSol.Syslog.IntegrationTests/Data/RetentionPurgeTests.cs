using System.Diagnostics;
using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class RetentionPurgeTests
{
    [Fact]
    public async Task PurgeOlderThanAsync_RemovesOldEvents_KeepsRecentOnes()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await db.Repository.AppendBatchAsync(
        [
            .. Enumerable.Range(0, 300).Select(i => SampleEvents.Minimal($"old {i}", receivedUtc: now.AddDays(-40).AddSeconds(i))),
            .. Enumerable.Range(0, 120).Select(i => SampleEvents.Minimal($"new {i}", receivedUtc: now.AddDays(-1).AddSeconds(i))),
        ], CancellationToken.None);

        long removed = await db.Repository.PurgeOlderThanAsync(now.AddDays(-30), CancellationToken.None);

        removed.Should().Be(300);
        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().Be(120);
    }

    [Fact]
    public async Task PurgeOlderThanAsync_KeepsEachWriteTransactionUnderTheLockBudget()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync(o => o.RetentionDeleteChunk = 2_000);
        var now = DateTimeOffset.UtcNow;
        await db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 20_000).Select(i => SampleEvents.Minimal($"e{i}", receivedUtc: now.AddYears(-1).AddSeconds(i))).ToList(),
            CancellationToken.None);

        // Measure the longest time the writer is unavailable during the purge by racing a
        // tiny append against it and recording the worst wait.
        TimeSpan worstAppendWait = TimeSpan.Zero;
        var purge = db.Repository.PurgeOlderThanAsync(now, CancellationToken.None);

        while (!purge.IsCompleted)
        {
            var sw = Stopwatch.StartNew();
            await db.Repository.AppendAsync(SampleEvents.Minimal("probe"), CancellationToken.None);
            sw.Stop();
            if (sw.Elapsed > worstAppendWait)
            {
                worstAppendWait = sw.Elapsed;
            }
        }

        await purge;
        // Generous ceiling: a 2,000-row chunked delete should never block a writer for
        // anywhere near this long. The point is that it is bounded, not unbounded.
        worstAppendWait.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }
}
