using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>PHASE_09 build item 6 — the Collector Health series over <c>collector_stat_samples</c>.</summary>
public sealed class SystemSeriesReaderTests : IAsyncLifetime
{
    private DashboardTestHarness _h = null!;
    private readonly DateTimeOffset _t0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => _h = await DashboardTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task Sample(DateTimeOffset at, long committed, int channelDepth, long dbBytes, long diskFree, long drops)
    {
        await using SqliteConnection c = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collector_stat_samples
                (taken_utc, committed_total, channel_depth, channel_capacity, spill_frames, spill_bytes,
                 database_bytes, disk_free_bytes, drops_total, active_connections, quarantined_sources)
            VALUES ($t, $c, $d, 100000, 0, 0, $db, $disk, $drops, 0, 0);
            """;
        cmd.Parameters.AddWithValue("$t", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$c", committed);
        cmd.Parameters.AddWithValue("$d", channelDepth);
        cmd.Parameters.AddWithValue("$db", dbBytes);
        cmd.Parameters.AddWithValue("$disk", diskFree);
        cmd.Parameters.AddWithValue("$drops", drops);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Latest_ReturnsTheNewestSample()
    {
        await Sample(_t0, 100, 5, 1000, 9000, 0);
        await Sample(_t0.AddMinutes(1), 400, 9, 1100, 8900, 2);

        CollectorSample? latest = await _h.SystemSeries.LatestAsync(CancellationToken.None);

        latest.Should().NotBeNull();
        latest!.CommittedTotal.Should().Be(400);
        latest.ChannelDepth.Should().Be(9);
        latest.DropsTotal.Should().Be(2);
    }

    [Fact]
    public async Task Latest_OnAnEmptyTable_IsNull()
    {
        (await _h.SystemSeries.LatestAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Series_ForIngestRate_DifferentiatesCommittedBetweenSamples()
    {
        // 60 s apart, +600 committed each step → 10 events/sec.
        await Sample(_t0, 0, 0, 0, 0, 0);
        await Sample(_t0.AddSeconds(60), 600, 0, 0, 0, 0);
        await Sample(_t0.AddSeconds(120), 1200, 0, 0, 0, 0);

        AggregationResult series = await _h.SystemSeries.SeriesAsync(
            SystemMetric.IngestRate, _t0, _t0.AddSeconds(180), BucketInterval.OneMinute, CancellationToken.None);

        series.Points.Should().NotBeEmpty();
        series.Points.Should().OnlyContain(p => Math.Abs(p.Value - 10.0) < 1e-6);
    }

    [Fact]
    public async Task Series_ForAGauge_AveragesTheValuePerBucket()
    {
        await Sample(_t0, 0, 10, 0, 0, 0);
        await Sample(_t0.AddSeconds(20), 0, 20, 0, 0, 0);
        await Sample(_t0.AddSeconds(40), 0, 30, 0, 0, 0);

        AggregationResult series = await _h.SystemSeries.SeriesAsync(
            SystemMetric.ChannelDepth, _t0, _t0.AddSeconds(60), BucketInterval.OneMinute, CancellationToken.None);

        series.Points.Should().ContainSingle();
        series.Points[0].Value.Should().BeApproximately(20, 1e-9);
    }

    [Fact]
    public async Task Prune_RemovesSamplesBeforeTheCutoff()
    {
        await Sample(_t0, 0, 0, 0, 0, 0);
        await Sample(_t0.AddHours(2), 0, 0, 0, 0, 0);

        int removed = await _h.SystemSeries.PruneAsync(_t0.AddHours(1), CancellationToken.None);

        removed.Should().Be(1);
        (await _h.SystemSeries.RangeAsync(_t0.AddYears(-1), _t0.AddYears(1), CancellationToken.None)).Should().ContainSingle();
    }
}
