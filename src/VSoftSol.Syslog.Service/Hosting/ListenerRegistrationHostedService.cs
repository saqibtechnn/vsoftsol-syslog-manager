using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Listeners;
using VSoftSol.Syslog.Ingestion;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// v1.1 — P2-1 (`docs/evidence/phase-02/known-issues.md`): upserts one <c>listeners</c> row
/// per protocol from configuration and populates <see cref="ListenerIdRegistry"/>, so
/// <c>events.listener_id</c> stops being permanently NULL. Uses the <em>configured</em>
/// bind address/port rather than each <see cref="ISyslogListener.BoundPort"/> — a real
/// install never configures an ephemeral port 0, so this registration does not need to wait
/// for <c>IngestionHostedService</c> to finish starting listeners; it only needs the
/// database, so it is registered after <c>DatabaseInitializer</c> and can run independently
/// of listener startup order.
/// </summary>
public sealed class ListenerRegistrationHostedService : IHostedService
{
    private readonly SqliteListenerStore _store;
    private readonly ListenerIdRegistry _registry;
    private readonly IngestionOptions _ingestion;
    private readonly TlsOptions _tls;
    private readonly SnmpOptions _snmp;
    private readonly WinEventLogOptions _winEventLog;

    public ListenerRegistrationHostedService(
        SqliteListenerStore store,
        ListenerIdRegistry registry,
        IOptions<IngestionOptions> ingestion,
        IOptions<TlsOptions> tls,
        IOptions<SnmpOptions> snmp,
        IOptions<WinEventLogOptions> winEventLog)
    {
        _store = store;
        _registry = registry;
        _ingestion = ingestion.Value;
        _tls = tls.Value;
        _snmp = snmp.Value;
        _winEventLog = winEventLog.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RegisterAsync(Protocol.Udp, _ingestion.UdpBindAddress, _ingestion.UdpPort, _ingestion.UdpEnabled, cancellationToken)
            .ConfigureAwait(false);
        await RegisterAsync(Protocol.Tcp, _ingestion.TcpBindAddress, _ingestion.TcpPort, _ingestion.TcpEnabled, cancellationToken)
            .ConfigureAwait(false);
        await RegisterAsync(Protocol.Tls, _tls.BindAddress, _tls.Port, _tls.Enabled, cancellationToken).ConfigureAwait(false);
        await RegisterAsync(Protocol.Snmp, _snmp.BindAddress, _snmp.Port, _snmp.Enabled, cancellationToken).ConfigureAwait(false);
        await RegisterAsync(Protocol.WinEventLog, _winEventLog.BindAddress, _winEventLog.Port, _winEventLog.Enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RegisterAsync(Protocol protocol, string bindAddress, int port, bool enabled, CancellationToken cancellationToken)
    {
        long listenerId = await _store.UpsertAsync(protocol, bindAddress, port, enabled, cancellationToken).ConfigureAwait(false);
        _registry.SetId(protocol, listenerId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
