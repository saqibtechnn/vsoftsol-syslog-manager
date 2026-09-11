using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Owns the ingestion lifecycle (PHASE_02 items 6 &amp; 7). On start it replays any spill
/// segments a previous run left behind <em>before</em> opening the listeners; on stop it
/// stops accepting, lets the pipeline drain the channel and the spill queue within
/// <see cref="IngestionOptions.ShutdownDrainTimeout"/>, and logs an overrun.
/// </summary>
public sealed class IngestionHostedService : IHostedService, IDisposable
{
    private readonly IReadOnlyList<ISyslogListener> _listeners;
    private readonly IngestionPipeline _pipeline;
    private readonly IngestionChannel _channel;
    private readonly DiskSpillQueue _spill;
    private readonly IngestionStatistics _stats;
    private readonly ListenerHealthRegistry _health;
    private readonly IngestionOptions _options;
    private readonly TlsOptions _tlsOptions;
    private readonly SnmpOptions _snmpOptions;
    private readonly WinEventLogOptions _winEventLogOptions;
    private readonly ILogger<IngestionHostedService> _logger;

    private readonly CancellationTokenSource _pipelineCts = new();
    private Task _pipelineTask = Task.CompletedTask;

    public IngestionHostedService(
        IEnumerable<ISyslogListener> listeners,
        IngestionPipeline pipeline,
        IngestionChannel channel,
        DiskSpillQueue spill,
        IngestionStatistics stats,
        ListenerHealthRegistry health,
        IOptions<IngestionOptions> options,
        IOptions<TlsOptions> tlsOptions,
        IOptions<SnmpOptions> snmpOptions,
        IOptions<WinEventLogOptions> winEventLogOptions,
        ILogger<IngestionHostedService> logger)
    {
        _listeners = listeners.ToList();
        _pipeline = pipeline;
        _channel = channel;
        _spill = spill;
        _stats = stats;
        _health = health;
        _options = options.Value;
        _tlsOptions = tlsOptions.Value;
        _snmpOptions = snmpOptions.Value;
        _winEventLogOptions = winEventLogOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        long carried = await _spill.RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (carried > 0)
        {
            _logger.LogInformation("Replaying {Count} spilled frame(s) from a previous run before opening listeners.", carried);
        }

        _pipelineTask = Task.Run(() => _pipeline.RunAsync(_pipelineCts.Token), CancellationToken.None);

        int started = 0;
        foreach (ISyslogListener listener in _listeners)
        {
            if (!IsEnabled(listener.Protocol))
            {
                continue;
            }

            await listener.StartAsync(cancellationToken).ConfigureAwait(false);
            _health.MarkRunning(listener.Name);
            started++;
        }

        _logger.LogInformation("Ingestion started: {Listeners} listener(s).", started);
    }

    private bool IsEnabled(Protocol protocol) => protocol switch
    {
        Protocol.Udp => _options.UdpEnabled,
        Protocol.Tcp => _options.TcpEnabled,
        Protocol.Tls => _tlsOptions.Enabled,
        Protocol.Snmp => _snmpOptions.Enabled,
        Protocol.WinEventLog => _winEventLogOptions.Enabled,
        _ => false,
    };

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ingestion stopping: closing listeners, then draining.");

        foreach (ISyslogListener listener in _listeners)
        {
            try
            {
                await listener.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Listener {Name} did not stop cleanly.", listener.Name);
            }
            finally
            {
                _health.MarkStopped(listener.Name);
            }
        }

        _channel.Complete();

        try
        {
            await _pipelineTask.WaitAsync(_options.ShutdownDrainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            IngestionStatsSnapshot s = _stats.Snapshot();
            _logger.LogWarning(
                "Drain timeout ({Timeout}) exceeded on shutdown. {InFlight} frame(s) remain — {Spill} on disk, safe for the next start.",
                _options.ShutdownDrainTimeout, s.InFlight, s.SpillFrameCount);
            await _pipelineCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pipelineTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (OperationCanceledException)
        {
            await _pipelineCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            // Flush, but do not dispose — the DI container owns the spill queue's lifetime.
            await _spill.ForceFlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error flushing the spill queue on shutdown.");
        }

        IngestionStatsSnapshot final = _stats.Snapshot();
        _logger.LogInformation(
            "Ingestion stopped. received={Received} committed={Committed} dropped={Dropped} failed={Failed} in-flight={InFlight}.",
            final.Total.Received, final.Total.Committed, final.Total.Dropped, final.Total.Failed, final.InFlight);
    }

    public void Dispose() => _pipelineCts.Dispose();
}
