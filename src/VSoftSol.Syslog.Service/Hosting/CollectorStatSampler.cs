using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Ingestion;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Writes a <c>collector_stat_samples</c> row every <see cref="CollectorStatOptions.SampleInterval"/>
/// from the live ingestion counters plus the database file size and the data-drive free
/// space (PHASE_09 build item 6 — the Collector Health dashboard). Runs in the collector
/// host only; when the Web host runs standalone there is no collector and no sampler, so
/// the table stays empty and the Collector Health widgets show their empty state until a
/// collector has run. Samples older than <see cref="CollectorStatOptions.Retention"/> are
/// pruned on each tick.
/// </summary>
public sealed class CollectorStatSampler : BackgroundService
{
    private readonly IngestionStatistics _stats;
    private readonly SqliteConnectionFactory _factory;
    private readonly SystemSeriesReader _series;
    private readonly SqliteDataOptions _dataOptions;
    private readonly CollectorStatOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CollectorStatSampler> _logger;

    public CollectorStatSampler(
        IngestionStatistics stats,
        SqliteConnectionFactory factory,
        SystemSeriesReader series,
        IOptions<SqliteDataOptions> dataOptions,
        IOptions<CollectorStatOptions> options,
        TimeProvider time,
        ILogger<CollectorStatSampler> logger)
    {
        _stats = stats;
        _factory = factory;
        _series = series;
        _dataOptions = dataOptions.Value;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Collector-stat sampler started (every {Interval}).", _options.SampleInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Collector-stat sample failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_options.SampleInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Collector-stat sampler stopped.");
    }

    /// <summary>One sample. Public so a test can drive it on a virtual clock.</summary>
    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        IngestionStatsSnapshot snapshot = _stats.Snapshot();
        long dbBytes = FileBytes(_dataOptions.DatabasePath);
        long diskFree = DriveFreeBytes(_dataOptions.DatabasePath);

        await using (IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false))
        await using (SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO collector_stat_samples
                    (taken_utc, committed_total, channel_depth, channel_capacity, spill_frames, spill_bytes,
                     database_bytes, disk_free_bytes, drops_total, active_connections, quarantined_sources)
                VALUES
                    ($t, $committed, $depth, $capacity, $spillF, $spillB, $db, $disk, $drops, $conns, $quar);
                """;
            command.Parameters.AddWithValue("$t", _time.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$committed", snapshot.Total.Committed);
            command.Parameters.AddWithValue("$depth", snapshot.ChannelDepth);
            command.Parameters.AddWithValue("$capacity", snapshot.ChannelCapacity);
            command.Parameters.AddWithValue("$spillF", snapshot.SpillFrameCount);
            command.Parameters.AddWithValue("$spillB", snapshot.SpillBytes);
            command.Parameters.AddWithValue("$db", dbBytes);
            command.Parameters.AddWithValue("$disk", diskFree);
            command.Parameters.AddWithValue("$drops", snapshot.Total.Dropped);
            command.Parameters.AddWithValue("$conns", snapshot.ActiveTcpConnections);
            command.Parameters.AddWithValue("$quar", snapshot.QuarantinedSources);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int pruned = await _series.PruneAsync(_time.GetUtcNow() - _options.Retention, cancellationToken).ConfigureAwait(false);
        if (pruned > 0)
        {
            _logger.LogDebug("Pruned {Count} collector-stat samples past retention.", pruned);
        }
    }

    private static long FileBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long DriveFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }
}
