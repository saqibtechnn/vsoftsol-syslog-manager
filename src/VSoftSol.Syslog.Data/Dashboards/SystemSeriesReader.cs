using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>One row of <c>collector_stat_samples</c>.</summary>
public sealed record CollectorSample
{
    public required DateTimeOffset TakenUtc { get; init; }

    public long CommittedTotal { get; init; }

    public int ChannelDepth { get; init; }

    public int ChannelCapacity { get; init; }

    public long SpillFrames { get; init; }

    public long SpillBytes { get; init; }

    public long DatabaseBytes { get; init; }

    public long DiskFreeBytes { get; init; }

    public long DropsTotal { get; init; }

    public int ActiveConnections { get; init; }

    public int QuarantinedSources { get; init; }

    /// <summary>The instantaneous value of one metric (rate metrics need two samples — see the reader).</summary>
    public double ValueOf(SystemMetric metric) => metric switch
    {
        SystemMetric.IngestRate => 0,
        SystemMetric.ChannelDepth => ChannelDepth,
        SystemMetric.SpillFrames => SpillFrames,
        SystemMetric.SpillBytes => SpillBytes,
        SystemMetric.DatabaseBytes => DatabaseBytes,
        SystemMetric.DiskFreeBytes => DiskFreeBytes,
        SystemMetric.DropsTotal => DropsTotal,
        SystemMetric.ActiveConnections => ActiveConnections,
        SystemMetric.QuarantinedSources => QuarantinedSources,
        _ => 0,
    };
}

/// <summary>
/// Reads the collector-health sample table for the Collector Health dashboard
/// (PHASE_09 build item 6). The samples are process-wide operational counters, not
/// per-tenant data, so there is no scope filter. Rate metrics (<see cref="SystemMetric.IngestRate"/>,
/// <see cref="SystemMetric.DropsTotal"/> shown as a rate) are differentiated between
/// consecutive samples; everything else is a gauge averaged per bucket.
/// </summary>
public sealed class SystemSeriesReader
{
    private readonly SqliteConnectionFactory _factory;

    public SystemSeriesReader(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    private const string SelectColumns =
        "taken_utc, committed_total, channel_depth, channel_capacity, spill_frames, spill_bytes, " +
        "database_bytes, disk_free_bytes, drops_total, active_connections, quarantined_sources";

    public async Task<CollectorSample?> LatestAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM collector_stat_samples ORDER BY taken_utc DESC LIMIT 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Every sample in <c>[fromUtc, toUtc)</c>, oldest first.</summary>
    public async Task<IReadOnlyList<CollectorSample>> RangeAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM collector_stat_samples " +
            "WHERE taken_utc >= $from AND taken_utc < $to ORDER BY taken_utc;";
        command.Parameters.AddWithValue("$from", StorageFormat.Timestamp(fromUtc));
        command.Parameters.AddWithValue("$to", StorageFormat.Timestamp(toUtc));

        var result = new List<CollectorSample>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    /// <summary>
    /// A time-bucketed series for one metric. Buckets that had no sample are absent (the
    /// renderer fills them with zero). Rate metrics use the delta between the first and last
    /// sample of the bucket over their elapsed time.
    /// </summary>
    public async Task<AggregationResult> SeriesAsync(
        SystemMetric metric, DateTimeOffset fromUtc, DateTimeOffset toUtc, BucketInterval bucket, CancellationToken cancellationToken)
    {
        BucketPlan plan = TimeBucketing.Plan(fromUtc, toUtc, bucket == BucketInterval.None ? BucketInterval.Auto : bucket);
        IReadOnlyList<CollectorSample> samples = await RangeAsync(fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
        if (samples.Count == 0)
        {
            return AggregationResult.Empty with { Plan = plan };
        }

        bool rate = metric == SystemMetric.IngestRate;

        // Per-sample instantaneous value: a gauge reading, or a committed-delta rate versus
        // the previous sample. Then average into buckets.
        var perBucket = new Dictionary<int, (double Sum, int Count)>();
        for (int i = 0; i < samples.Count; i++)
        {
            CollectorSample s = samples[i];
            double value;
            if (rate)
            {
                if (i == 0)
                {
                    continue; // no previous sample to differentiate against
                }

                CollectorSample prev = samples[i - 1];
                double seconds = Math.Max(1, (s.TakenUtc - prev.TakenUtc).TotalSeconds);
                value = Math.Max(0, s.CommittedTotal - prev.CommittedTotal) / seconds;
            }
            else
            {
                value = s.ValueOf(metric);
            }

            int idx = Math.Clamp(plan.IndexOf(s.TakenUtc), 0, plan.Count - 1);
            (double sum, int count) = perBucket.GetValueOrDefault(idx);
            perBucket[idx] = (sum + value, count + 1);
        }

        var points = perBucket
            .OrderBy(kv => kv.Key)
            .Select(kv => new AggPoint(null, kv.Key, kv.Value.Count == 0 ? 0 : kv.Value.Sum / kv.Value.Count))
            .ToList();

        return new AggregationResult { Points = points, Plan = plan, Truncated = false, GroupsOmitted = 0 };
    }

    /// <summary>Deletes samples older than <paramref name="cutoffUtc"/>. Returns the number removed.</summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM collector_stat_samples WHERE taken_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", StorageFormat.Timestamp(cutoffUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CollectorSample Map(SqliteDataReader reader) => new()
    {
        TakenUtc = StorageFormat.ParseTimestamp(reader.GetString(0)),
        CommittedTotal = reader.GetInt64(1),
        ChannelDepth = (int)reader.GetInt64(2),
        ChannelCapacity = (int)reader.GetInt64(3),
        SpillFrames = reader.GetInt64(4),
        SpillBytes = reader.GetInt64(5),
        DatabaseBytes = reader.GetInt64(6),
        DiskFreeBytes = reader.GetInt64(7),
        DropsTotal = reader.GetInt64(8),
        ActiveConnections = (int)reader.GetInt64(9),
        QuarantinedSources = (int)reader.GetInt64(10),
    };
}
