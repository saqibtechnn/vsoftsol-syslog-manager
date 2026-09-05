using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// Keeps the FTS index current without slowing ingestion. A per-row AFTER INSERT trigger
/// is ~10x too slow to meet the 20,000 rows/sec insert gate, so <see cref="SqliteLogRepository"/>
/// writes only the event store and this service copies new rows into <c>events_fts</c> on a
/// short interval. Search results therefore trail ingestion by at most one interval
/// (default one second). See ADR 0009.
/// </summary>
public sealed class SearchIndexMaintainer : BackgroundService
{
    private readonly SqliteLogRepository _repository;
    private readonly SqliteDataOptions _options;
    private readonly ILogger<SearchIndexMaintainer> _logger;

    public SearchIndexMaintainer(
        SqliteLogRepository repository,
        IOptions<SqliteDataOptions> options,
        ILogger<SearchIndexMaintainer> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Search-index maintainer started (interval {Interval}, batch {Batch}).",
            _options.SearchIndexInterval, _options.SearchIndexBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            int indexed;
            try
            {
                indexed = await _repository
                    .SyncSearchIndexAsync(_options.SearchIndexBatchSize, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search-index maintenance pass failed; retrying after the interval.");
                indexed = 0;
            }

            // If the pass filled its batch there is more to do — loop again immediately.
            if (indexed >= _options.SearchIndexBatchSize)
            {
                continue;
            }

            // Caught up: tidy the WAL, then wait for more.
            try
            {
                await _repository.CheckpointAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WAL checkpoint failed; continuing.");
            }

            try
            {
                await Task.Delay(_options.SearchIndexInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
