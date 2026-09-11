using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Web.Hardening;

public sealed record SelfMonitoringView(
    CollectorSample? Latest,
    DateTimeOffset? LastArchiveUtc,
    int TamperedArchiveCount);

/// <summary>
/// The self-monitoring page (PHASE_11 item 5). The Web host is a separate process from the
/// collector, so it cannot read the collector's in-memory <c>IngestionStatistics</c>
/// directly — it reads the same <c>collector_stat_samples</c> table the Phase 9 Collector
/// Health dashboard widget already reads, written every tick by the collector-host-only
/// <c>CollectorStatSampler</c>. An empty <see cref="SelfMonitoringView.Latest"/> means the
/// collector host has not sampled yet (e.g. the Web host is being run standalone).
/// </summary>
public sealed class SelfMonitoringViewService
{
    private readonly SystemSeriesReader _series;
    private readonly SqliteConnectionFactory _factory;

    public SelfMonitoringViewService(SystemSeriesReader series, SqliteConnectionFactory factory)
    {
        _series = series;
        _factory = factory;
    }

    public async Task<SelfMonitoringView> GetAsync(CancellationToken cancellationToken)
    {
        CollectorSample? latest = await _series.LatestAsync(cancellationToken).ConfigureAwait(false);
        (DateTimeOffset? lastArchive, int tampered) = await ReadArchiveStatusAsync(cancellationToken).ConfigureAwait(false);
        return new SelfMonitoringView(latest, lastArchive, tampered);
    }

    private async Task<(DateTimeOffset? LastArchive, int Tampered)> ReadArchiveStatusAsync(CancellationToken ct)
    {
        try
        {
            await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
            DateTimeOffset? last = null;
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT MAX(created_utc) FROM archives;";
                object? r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (r is string s)
                {
                    last = DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            await using SqliteCommand tamperedCmd = connection.CreateCommand();
            tamperedCmd.CommandText = "SELECT COUNT(*) FROM archives WHERE status = 'tamper_detected';";
            object? t = await tamperedCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return (last, Convert.ToInt32(t, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (SqliteException)
        {
            return (null, 0);
        }
    }
}
