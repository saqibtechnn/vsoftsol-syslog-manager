using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>
/// FTS indexing is deferred off the ingest path (ADR 0009). These assert it stays
/// eventually-consistent, advances its watermark, and is chunk-bounded.
/// </summary>
public sealed class SearchIndexMaintainerTests
{

    [Fact]
    public async Task NewEvents_AreNotSearchableUntilSynced_ThenAre()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Repository.AppendAsync(SampleEvents.Minimal("needle in the haystack"), CancellationToken.None);

        (await Count(db, "needle")).Should().Be(0, "the maintainer has not run yet");

        int indexed = await db.SyncSearchAsync();

        indexed.Should().Be(1);
        (await Count(db, "needle")).Should().Be(1);
    }

    [Fact]
    public async Task Sync_IsIdempotent_AndAdvancesTheWatermark()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 40).Select(i => SampleEvents.Minimal($"m{i}")).ToList(), CancellationToken.None);

        (await db.Repository.SyncSearchIndexAsync(25, CancellationToken.None)).Should().Be(25);
        (await db.Repository.SyncSearchIndexAsync(25, CancellationToken.None)).Should().Be(15);
        (await db.Repository.SyncSearchIndexAsync(25, CancellationToken.None)).Should().Be(0);

        (await Watermark(db)).Should().Be(40);
    }

    [Fact]
    public async Task Maintainer_CatchesUp_WhenStartedAgainstABacklog()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync(o =>
        {
            o.SearchIndexBatchSize = 1_000;
            o.SearchIndexInterval = TimeSpan.FromMilliseconds(20);
        });
        await db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 5_000).Select(i => SampleEvents.Minimal($"backlog {i}")).ToList(), CancellationToken.None);

        int passes = 0;
        int total = 0;
        int indexed;
        do
        {
            indexed = await db.Repository.SyncSearchIndexAsync(db.Options.SearchIndexBatchSize, CancellationToken.None);
            total += indexed;
            passes++;
        }
        while (indexed == db.Options.SearchIndexBatchSize && passes < 20);

        total.Should().Be(5_000);
        (await Watermark(db)).Should().Be(5_000);
        (await Count(db, "backlog")).Should().Be(5_000);
    }

    [Fact]
    public async Task Maintainer_AsHostedService_IndexesNewEvents_AndStopsCleanly()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync(o =>
            o.SearchIndexInterval = TimeSpan.FromMilliseconds(20));
        await db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 200).Select(i => SampleEvents.Minimal($"served {i}")).ToList(), CancellationToken.None);

        var maintainer = new SearchIndexMaintainer(
            db.Repository, Options.Create(db.Options), NullLogger<SearchIndexMaintainer>.Instance);

        await maintainer.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(async () => await Watermark(db) == 200, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await maintainer.StopAsync(CancellationToken.None);
        }

        (await Count(db, "served")).Should().Be(200);
    }

    [Fact]
    public async Task Purge_RemovesIndexedRowsFromFts_NoPhantomSearchHits()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Repository.AppendBatchAsync(
        [
            .. Enumerable.Range(0, 50).Select(i => SampleEvents.Minimal($"ancient {i}", receivedUtc: now.AddYears(-2))),
            .. Enumerable.Range(0, 20).Select(i => SampleEvents.Minimal($"recent {i}", receivedUtc: now)),
        ], CancellationToken.None);
        await db.SyncSearchAsync();

        (await Count(db, "ancient")).Should().Be(50);

        await db.Repository.PurgeOlderThanAsync(now.AddDays(-1), CancellationToken.None);

        (await Count(db, "ancient")).Should().Be(0, "purged rows must leave no FTS phantom");
        (await Count(db, "recent")).Should().Be(20);
    }

    private static async Task<long> Count(SqliteTestDatabase db, string text) =>
        await db.Repository.CountAsync(new LogQuery { FullText = text }, CancellationToken.None);

    private static async Task<long> Watermark(SqliteTestDatabase db)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT last_indexed_event_id FROM fts_state WHERE id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("condition not met in time");
    }
}
