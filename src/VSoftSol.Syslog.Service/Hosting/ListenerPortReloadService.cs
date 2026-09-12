using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Listeners;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>The outcome of one <see cref="ListenerPortReloadService.ApplyAsync"/> call.</summary>
/// <param name="Applied">One entry per protocol whose port actually changed, e.g. "UDP 514 → 5514".</param>
/// <param name="Errors">One entry per requested change that failed (the listener is unaffected).</param>
/// <param name="NotApplicable">True when this process hosts no collector runtime at all — a
/// port change here always needs a restart, the same as before v1.1.</param>
public sealed record PortReloadResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Errors, bool NotApplicable = false)
{
    public bool Success => !NotApplicable && Errors.Count == 0;
}

/// <summary>
/// v1.1 — live listener port changes (closes the RELEASE_NOTES.md v1.0.0 known limitation,
/// UDP/TCP scope; TLS/SNMP/WinEventLog remain restart-tier, unchanged). ADR 0005 means the
/// process this runs in (production <c>Web.exe</c>, or console-mode <c>Service.exe</c>) is
/// the same process that holds the listener sockets, so "live" here is an in-process rebind
/// (<see cref="UdpSyslogListener.RebindAsync"/>/<see cref="TcpSyslogListener.RebindAsync"/>),
/// never a cross-process signal or an SCM privilege grant.
///
/// Order matters: the live rebind is attempted first, and the new port is persisted to
/// <see cref="BootstrapConfigOverrides"/> (so a future restart keeps it) only once the
/// rebind has actually succeeded — never the other way around, which could leave the
/// on-disk config and the running listener disagreeing about which port is real.
/// </summary>
public sealed class ListenerPortReloadService
{
    private readonly IReadOnlyList<ISyslogListener> _listeners;
    private readonly IOptions<IngestionOptions> _ingestion;
    private readonly IOptions<ActionExecutorOptions> _actionOptions;
    private readonly IOptions<CollectorOptions> _collector;
    private readonly SqliteListenerStore _listenerStore;
    private readonly ListenerIdRegistry _listenerIds;
    private readonly ILogger<ListenerPortReloadService> _logger;

    public ListenerPortReloadService(
        IEnumerable<ISyslogListener> listeners,
        IOptions<IngestionOptions> ingestion,
        IOptions<ActionExecutorOptions> actionOptions,
        IOptions<CollectorOptions> collector,
        SqliteListenerStore listenerStore,
        ListenerIdRegistry listenerIds,
        ILogger<ListenerPortReloadService> logger)
    {
        _listeners = listeners.ToList();
        _ingestion = ingestion;
        _actionOptions = actionOptions;
        _collector = collector;
        _listenerStore = listenerStore;
        _listenerIds = listenerIds;
        _logger = logger;
    }

    /// <summary>True only in the process actually holding the UDP/TCP listener sockets.</summary>
    public bool CanApplyLive => _listeners.Any(l => l.Protocol is Protocol.Udp or Protocol.Tcp);

    public async Task<PortReloadResult> ApplyAsync(int? newUdpPort, int? newTcpPort, CancellationToken cancellationToken)
    {
        if (!CanApplyLive)
        {
            return new PortReloadResult([], ["This process is not hosting the collector runtime — a restart is required for a port change to take effect."], NotApplicable: true);
        }

        var applied = new List<string>();
        var errors = new List<string>();
        int? appliedUdpPort = null;
        int? appliedTcpPort = null;

        if (newUdpPort is { } udpPort)
        {
            ISyslogListener? listener = _listeners.FirstOrDefault(l => l.Protocol == Protocol.Udp);
            if (listener is UdpSyslogListener udp && udp.BoundPort != udpPort)
            {
                int oldPort = udp.BoundPort;
                try
                {
                    await udp.RebindAsync(udpPort, cancellationToken).ConfigureAwait(false);
                    RelinkLocalSyslogEndpoint(_ingestion.Value.UdpBindAddress, oldPort, udp.BoundPort);
                    _ingestion.Value.UdpPort = udp.BoundPort;
                    await RegisterListenerAsync(Protocol.Udp, _ingestion.Value.UdpBindAddress, udp.BoundPort, cancellationToken)
                        .ConfigureAwait(false);
                    applied.Add($"UDP {oldPort} → {udp.BoundPort}");
                    appliedUdpPort = udp.BoundPort;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    _logger.LogWarning(ex, "Failed to rebind the UDP listener to port {Port}.", udpPort);
                    errors.Add($"UDP port {udpPort}: {DescribeBindFailure(ex)}");
                }
            }
        }

        if (newTcpPort is { } tcpPort)
        {
            ISyslogListener? listener = _listeners.FirstOrDefault(l => l.Protocol == Protocol.Tcp);
            if (listener is TcpSyslogListener tcp && tcp.BoundPort != tcpPort)
            {
                int oldPort = tcp.BoundPort;
                try
                {
                    await tcp.RebindAsync(tcpPort, cancellationToken).ConfigureAwait(false);
                    RelinkLocalSyslogEndpoint(_ingestion.Value.TcpBindAddress, oldPort, tcp.BoundPort);
                    _ingestion.Value.TcpPort = tcp.BoundPort;
                    await RegisterListenerAsync(Protocol.Tcp, _ingestion.Value.TcpBindAddress, tcp.BoundPort, cancellationToken)
                        .ConfigureAwait(false);
                    applied.Add($"TCP {oldPort} → {tcp.BoundPort}");
                    appliedTcpPort = tcp.BoundPort;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    _logger.LogWarning(ex, "Failed to rebind the TCP listener to port {Port}.", tcpPort);
                    errors.Add($"TCP port {tcpPort}: {DescribeBindFailure(ex)}");
                }
            }
        }

        if (appliedUdpPort is not null || appliedTcpPort is not null)
        {
            try
            {
                await BootstrapConfigOverrides.UpdateIngestionPortsAsync(
                    _collector.Value.DataDirectory,
                    appliedUdpPort ?? _ingestion.Value.UdpPort,
                    appliedTcpPort ?? _ingestion.Value.TcpPort,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Port change applied live but could not be persisted for the next restart.");
                errors.Add("The change is live now, but saving it for the next restart failed — check the data directory's disk space and permissions.");
            }
        }

        return new PortReloadResult(applied, errors);
    }

    /// <summary>
    /// v1.1 — P2-1: after a successful live rebind, the `listeners` table and
    /// <see cref="ListenerIdRegistry"/> must reflect the new port too — otherwise every
    /// event received after the rebind would keep carrying the stale, pre-rebind
    /// <c>listener_id</c>. A changed port is a genuinely new listener identity (a new row,
    /// not an in-place rewrite), so events already stored under the old id keep resolving
    /// correctly.
    /// </summary>
    private async Task RegisterListenerAsync(Protocol protocol, string bindAddress, int port, CancellationToken cancellationToken)
    {
        long listenerId = await _listenerStore.UpsertAsync(protocol, bindAddress, port, enabled: true, cancellationToken)
            .ConfigureAwait(false);
        _listenerIds.SetId(protocol, listenerId);
    }

    private void RelinkLocalSyslogEndpoint(string bindAddress, int oldPort, int newPort)
    {
        List<string> endpoints = _actionOptions.Value.LocalSyslogEndpoints;
        string oldValue = $"{bindAddress}:{oldPort}";
        int index = endpoints.FindIndex(e => string.Equals(e, oldValue, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            endpoints[index] = $"{bindAddress}:{newPort}";
        }
    }

    private static string DescribeBindFailure(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => "that port is already in use.",
        SocketException { SocketErrorCode: SocketError.AccessDenied } =>
            "permission denied — ports below 1024 need elevated rights; consider a higher port instead.",
        _ => ex.Message,
    };
}
