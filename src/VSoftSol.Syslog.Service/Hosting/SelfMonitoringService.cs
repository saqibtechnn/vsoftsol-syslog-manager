using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Core.SelfMonitoring;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The collector's self-monitoring loop (PHASE_11 items 5-7): every tick, reads the five
/// live health signals, runs them through the pure <see cref="SelfMonitoringEvaluator"/>,
/// and for each breach/clear transition both (a) appends a synthetic event into the
/// reserved <c>collector.health</c> stream — through the same repository, so it is
/// searchable, dashboardable, and alertable by the ordinary rules/alerts engine exactly
/// like any other event — and (b) raises the notification directly, so the self-alert
/// itself never depends on an operator having authored a matching alert definition first.
/// Collector-host only.
/// </summary>
public sealed class SelfMonitoringService : BackgroundService
{
    private readonly IngestionStatistics _stats;
    private readonly ListenerHealthRegistry _listenerHealth;
    private readonly ILogRepository _repository;
    private readonly SqliteConnectionFactory _factory;
    private readonly NotificationSink _notify;
    private readonly TimeProvider _time;
    private readonly CollectorOptions _collectorOptions;
    private readonly IngestionOptions _ingestionOptions;
    private readonly TlsOptions _tlsOptions;
    private readonly SnmpOptions _snmpOptions;
    private readonly WinEventLogOptions _winEventLogOptions;
    private readonly SelfMonitoringOptions _options;
    private readonly ILogger<SelfMonitoringService> _logger;

    private SelfMonitoringState _state = SelfMonitoringState.Empty;
    private long _lastDroppedTotal;
    private long? _reservedStreamId;

    public SelfMonitoringService(
        IngestionStatistics stats,
        ListenerHealthRegistry listenerHealth,
        ILogRepository repository,
        SqliteConnectionFactory factory,
        NotificationSink notify,
        TimeProvider time,
        IOptions<CollectorOptions> collectorOptions,
        IOptions<IngestionOptions> ingestionOptions,
        IOptions<TlsOptions> tlsOptions,
        IOptions<SnmpOptions> snmpOptions,
        IOptions<WinEventLogOptions> winEventLogOptions,
        IOptions<SelfMonitoringOptions> options,
        ILogger<SelfMonitoringService> logger)
    {
        _stats = stats;
        _listenerHealth = listenerHealth;
        _repository = repository;
        _factory = factory;
        _notify = notify;
        _time = time;
        _collectorOptions = collectorOptions.Value;
        _ingestionOptions = ingestionOptions.Value;
        _tlsOptions = tlsOptions.Value;
        _snmpOptions = snmpOptions.Value;
        _winEventLogOptions = winEventLogOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Self-monitoring tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        long? streamId = await ResolveReservedStreamIdAsync(cancellationToken).ConfigureAwait(false);
        if (streamId is null)
        {
            return; // migrations/seed have not run yet
        }

        IngestionStatsSnapshot snapshot = _stats.Snapshot();
        long dropDelta = Math.Max(0, snapshot.Total.Dropped - _lastDroppedTotal);
        _lastDroppedTotal = snapshot.Total.Dropped;
        int queuePercent = snapshot.ChannelCapacity > 0 ? (int)(100.0 * snapshot.ChannelDepth / snapshot.ChannelCapacity) : 0;
        long diskFree = SafeDiskFreeBytes(_collectorOptions.DataDirectory);
        IReadOnlyList<string> listenersDown = FindDownListeners();
        int archiveFailures = await CountRecentArchiveFailuresAsync(cancellationToken).ConfigureAwait(false);

        var health = new SelfMonitoringSnapshot(_time.GetUtcNow(), diskFree, dropDelta, queuePercent, listenersDown, archiveFailures);
        var thresholds = new SelfMonitoringThresholds
        {
            DiskFreeBytesMinimum = _options.DiskFreeBytesMinimum,
            QueueDepthPercentMax = _options.QueueDepthPercentMax,
            QueueDepthSustainedFor = _options.QueueDepthSustainedFor,
        };

        (SelfMonitoringState newState, IReadOnlyList<SelfMonitoringTransition> transitions) =
            SelfMonitoringEvaluator.Evaluate(health, thresholds, _state);
        _state = newState;

        foreach (SelfMonitoringTransition transition in transitions)
        {
            await EmitHealthEventAsync(streamId.Value, transition, cancellationToken).ConfigureAwait(false);

            NotificationLevel level = transition.Kind == SelfMonitoringTransitionKind.Cleared
                ? NotificationLevel.Info
                : transition.Severity is Severity.Error or Severity.Critical or Severity.Alert or Severity.Emergency
                    ? NotificationLevel.Critical
                    : NotificationLevel.Warning;

            await _notify(level, $"Collector health: {transition.Metric}", transition.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EmitHealthEventAsync(long streamId, SelfMonitoringTransition transition, CancellationToken cancellationToken)
    {
        string message = transition.Message;
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(message);

        var fields = new List<EventField>(transition.Fields.Count + 1);
        foreach ((string name, string value) in transition.Fields)
        {
            fields.Add(new EventField(name, value));
        }

        fields.Add(new EventField("self_monitoring_transition", transition.Kind.ToString()));

        var syslogEvent = new SyslogEvent
        {
            ReceivedUtc = _time.GetUtcNow(),
            SourceIp = "127.0.0.1",
            Hostname = "collector",
            AppName = "vsoftsol-collector",
            Facility = Facility.Syslogd,
            Severity = transition.Severity,
            // Not from any real listener — closest existing wire protocol, documented in
            // docs/evidence/phase-11/known-issues.md (B11-1) rather than widening the
            // `events.protocol` CHECK constraint, which would need a full table rebuild.
            Protocol = Protocol.Udp,
            Message = message,
            RawMessage = raw,
            ParseStatus = ParseStatus.Raw,
            Fields = fields,
            StreamIds = [streamId],
        };

        try
        {
            await _repository.AppendBatchAsync([syslogEvent], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append a self-monitoring health event ({Metric}); the notification was still raised.", transition.Metric);
        }
    }

    private List<string> FindDownListeners()
    {
        IReadOnlyDictionary<string, bool> running = _listenerHealth.Snapshot();
        bool AnyRunning(string prefix) => running.Any(kv => kv.Key.StartsWith(prefix + ":", StringComparison.Ordinal) && kv.Value);

        var down = new List<string>();
        if (_ingestionOptions.UdpEnabled && !AnyRunning("udp"))
        {
            down.Add("udp");
        }

        if (_ingestionOptions.TcpEnabled && !AnyRunning("tcp"))
        {
            down.Add("tcp");
        }

        if (_tlsOptions.Enabled && !AnyRunning("tls"))
        {
            down.Add("tls");
        }

        if (_snmpOptions.Enabled && !AnyRunning("snmp"))
        {
            down.Add("snmp");
        }

        if (_winEventLogOptions.Enabled && !AnyRunning("wineventlog"))
        {
            down.Add("wineventlog");
        }

        return down;
    }

    private async Task<int> CountRecentArchiveFailuresAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM archives
                 WHERE status IN ('tamper_detected', 'missing')
                   AND verified_utc > $since;
                """;
            command.Parameters.AddWithValue("$since",
                (_time.GetUtcNow() - _options.ArchiveVerificationLookback).ToString("O", CultureInfo.InvariantCulture));
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0; // the archives table may not exist yet on a very first tick before migrations settle
        }
    }

    private async Task<long?> ResolveReservedStreamIdAsync(CancellationToken cancellationToken)
    {
        if (_reservedStreamId is { } cached)
        {
            return cached;
        }

        try
        {
            await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT stream_id FROM streams WHERE name = $name;";
            command.Parameters.AddWithValue("$name", DatabaseSeeder.ReservedHealthStreamName);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is long id)
            {
                _reservedStreamId = id;
                return id;
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogDebug(ex, "Could not resolve the reserved health stream id yet.");
        }

        return null;
    }

    private static long SafeDiskFreeBytes(string dataDirectory)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? "." : dataDirectory)) ?? ".";
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue; // cannot determine — never falsely report a disk-space breach
        }
    }
}
