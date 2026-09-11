using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Hardening;

/// <summary>
/// Settings → Listeners (PHASE_11 items 1-3). Bind address, port, and protocol toggles are
/// technical, install-time settings read from the service configuration file — the same
/// disposition the product has used for the UDP/TCP listeners since Phase 2 — and are
/// shown here read-only with the setting name to change; the SNMP community string and the
/// TLS PFX password are secrets an Administrator sets from this page without touching a
/// file or restarting anything, since <see cref="SnmpCommunityProvider"/>/<see cref="TlsCertificateProvider"/>
/// re-resolve them on every use.
/// </summary>
public sealed class ListenerSettingsService
{
    private readonly TlsOptions _tls;
    private readonly SnmpOptions _snmp;
    private readonly WinEventLogOptions _winEventLog;
    private readonly SqliteSecretStore _secrets;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _currentUser;

    public ListenerSettingsService(
        IOptions<TlsOptions> tls, IOptions<SnmpOptions> snmp, IOptions<WinEventLogOptions> winEventLog,
        SqliteSecretStore secrets, SqliteAuditLog audit, CurrentUserAccessor currentUser)
    {
        _tls = tls.Value;
        _snmp = snmp.Value;
        _winEventLog = winEventLog.Value;
        _secrets = secrets;
        _audit = audit;
        _currentUser = currentUser;
    }

    public TlsOptions Tls => _tls;

    public SnmpOptions Snmp => _snmp;

    public WinEventLogOptions WinEventLog => _winEventLog;

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
