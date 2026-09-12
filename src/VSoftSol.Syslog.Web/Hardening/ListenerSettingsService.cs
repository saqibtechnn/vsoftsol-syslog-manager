using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Hardening;

/// <summary>
/// Settings → Listeners (PHASE_11 items 1-3, v1.1 UDP/TCP live ports). TLS/SNMP/WinEventLog
/// bind address, port, and protocol toggles remain technical, install-time settings read
/// from the service configuration file, shown here read-only with the setting name to
/// change; the SNMP community string and the TLS PFX password are secrets an Administrator
/// sets from this page without touching a file or restarting anything, since
/// <see cref="SnmpCommunityProvider"/>/<see cref="TlsCertificateProvider"/> re-resolve them
/// on every use. The core UDP/TCP syslog ports are the exception (v1.1): those can now be
/// changed here too, live, via <see cref="ListenerPortReloadService"/> — closing the
/// RELEASE_NOTES.md v1.0.0 known limitation for those two ports specifically.
/// </summary>
public sealed class ListenerSettingsService
{
    private readonly TlsOptions _tls;
    private readonly SnmpOptions _snmp;
    private readonly WinEventLogOptions _winEventLog;
    private readonly IngestionOptions _ingestion;
    private readonly ListenerPortReloadService _portReload;
    private readonly SqliteSecretStore _secrets;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _currentUser;

    public ListenerSettingsService(
        IOptions<TlsOptions> tls, IOptions<SnmpOptions> snmp, IOptions<WinEventLogOptions> winEventLog,
        IOptions<IngestionOptions> ingestion, ListenerPortReloadService portReload,
        SqliteSecretStore secrets, SqliteAuditLog audit, CurrentUserAccessor currentUser)
    {
        _tls = tls.Value;
        _snmp = snmp.Value;
        _winEventLog = winEventLog.Value;
        _ingestion = ingestion.Value;
        _portReload = portReload;
        _secrets = secrets;
        _audit = audit;
        _currentUser = currentUser;
    }

    public TlsOptions Tls => _tls;

    public SnmpOptions Snmp => _snmp;

    public WinEventLogOptions WinEventLog => _winEventLog;

    public IngestionOptions Ingestion => _ingestion;

    /// <summary>True only when this process holds the UDP/TCP listener sockets — a port
    /// change applies live here; otherwise (a standalone Web host) it still needs a restart.</summary>
    public bool CanApplyPortsLive => _portReload.CanApplyLive;

    /// <summary>
    /// Changes the UDP and/or TCP syslog listener port. Pass <see langword="null"/> for a
    /// protocol whose port is not being changed. Applies live when
    /// <see cref="CanApplyPortsLive"/> is true; every attempted change — accepted or
    /// refused — that reaches the reload service is audited under
    /// <see cref="AuditActions.ConfigChange"/>, the same as any other Settings write.
    /// </summary>
    public async Task<(bool Ok, string Message)> SetUdpTcpPortsAsync(int? udpPort, int? tcpPort, CancellationToken ct)
    {
        if (udpPort is null && tcpPort is null)
        {
            return (false, "No port change was requested.");
        }

        if (udpPort is { } u && u is < 1 or > 65535)
        {
            return (false, $"'{u}' is not a valid UDP port — enter a value from 1 to 65535.");
        }

        if (tcpPort is { } t && t is < 1 or > 65535)
        {
            return (false, $"'{t}' is not a valid TCP port — enter a value from 1 to 65535.");
        }

        PortReloadResult result = await _portReload.ApplyAsync(udpPort, tcpPort, ct).ConfigureAwait(false);

        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        await _audit.AppendAsync(
            new AuditEntry(
                AuditActions.ConfigChange, me.UserName, "listener_port", null,
                Detail: result.Success
                    ? $"Listener port(s) changed: {string.Join(", ", result.Applied)}"
                    : $"Listener port change refused: {string.Join("; ", result.Errors)}"),
            CancellationToken.None).ConfigureAwait(false);

        if (result.NotApplicable)
        {
            return (false, result.Errors[0]);
        }

        if (!result.Success)
        {
            return (false, string.Join(" ", result.Errors));
        }

        return (true, result.Applied.Count == 0
            ? "No change — the requested port(s) already match the current configuration."
            : $"{string.Join(", ", result.Applied)} — applied live, no restart needed.");
    }

    public async Task<string?> GetSnmpCommunityAsync(CancellationToken ct) =>
        string.IsNullOrWhiteSpace(_snmp.CommunitySecretName) ? null : await _secrets.GetAsync(_snmp.CommunitySecretName, ct).ConfigureAwait(false);

    public async Task<(bool Ok, string Message)> SetSnmpCommunityAsync(string community, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_snmp.CommunitySecretName))
        {
            return (false, "Set 'Snmp:CommunitySecretName' in the service configuration first (a one-time technical setup step).");
        }

        if (string.Equals(community.Trim(), "public", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "'public' is never accepted — choose a real community string.");
        }

        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        await _secrets.SetAsync(_snmp.CommunitySecretName, community.Trim(), me.UserName, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.SecretChange, me.UserName, "snmp_community", null, Detail: "SNMP community string changed"),
            CancellationToken.None).ConfigureAwait(false);
        return (true, "SNMP community updated — takes effect on the next received trap, no restart needed.");
    }
}
