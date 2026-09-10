using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>PHASE_09 build item 6 — the collector-health sampler writes one row per tick and prunes.</summary>
public sealed class CollectorStatSamplerTests : IAsyncLifetime
{
    private SqliteTestDatabase _db = null!;
    private IngestionStatistics _stats = null!;
    private SystemSeriesReader _series = null!;
    private FakeTimeProvider _clock = null!;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDatabase.CreateAsync();
        _stats = new IngestionStatistics();
        _series = new SystemSeriesReader(_db.Factory);
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private CollectorStatSampler Sampler(CollectorStatOptions? options = null) => new(
        _stats,
        _db.Factory,
        _series,
        Options.Create(_db.Options),
        Options.Create(options ?? new CollectorStatOptions()),
        _clock,
        NullLogger<CollectorStatSampler>.Instance);

    [Fact]
    public async Task Sample_WritesOneRow_FromTheLiveCounters()
    {
        _stats.SetChannel(depth: 42, capacity: 100_000);
        _stats.SetSpill(frames: 7, bytes: 2048);
        _stats.Listener("udp:514").AddCommitted(1500);

        await Sampler().SampleAsync(CancellationToken.None);

        CollectorSample? latest = await _series.LatestAsync(CancellationToken.None);
        latest.Should().NotBeNull();
        latest!.ChannelDepth.Should().Be(42);
        latest.SpillFrames.Should().Be(7);
        latest.SpillBytes.Should().Be(2048);
        latest.CommittedTotal.Should().Be(1500);
        latest.DatabaseBytes.Should().BeGreaterThan(0, "the migrated db file has a size");
    }

    [Fact]
    public async Task Sample_TwiceAcrossTime_ProducesASeriesTheReaderCanDifferentiate()
    {
        _stats.Listener("udp:514").AddCommitted(0);
        await Sampler().SampleAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(60));
        _stats.Listener("udp:514").AddCommitted(600);
        await Sampler().SampleAsync(CancellationToken.None);

        AggregationResult series = await _series.SeriesAsync(
            SystemMetric.IngestRate,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 0, 2, 0, TimeSpan.Zero),
            BucketInterval.OneMinute,
            CancellationToken.None);

        series.Points.Should().ContainSingle();
        series.Points[0].Value.Should().BeApproximately(10.0, 1e-6); // 600 events / 60 s
    }

    [Fact]
    public async Task Sample_PrunesRowsOlderThanRetention()
    {
        var options = new CollectorStatOptions { Retention = TimeSpan.FromMinutes(30) };

        await Sampler(options).SampleAsync(CancellationToken.None); // t=0
        _clock.Advance(TimeSpan.FromHours(1));
        await Sampler(options).SampleAsync(CancellationToken.None); // t=+1h, prunes t=0

        IReadOnlyList<CollectorSample> all = await _series.RangeAsync(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        all.Should().ContainSingle();
        all[0].TakenUtc.Should().Be(new DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.Zero));
    }
}
