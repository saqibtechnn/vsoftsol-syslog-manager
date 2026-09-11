using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Hardening;

/// <summary>Windows Event Log intake API keys (PHASE_11 item 3). Administrator-only —
/// registered under Settings → Listeners alongside the endpoint they authenticate.</summary>
public sealed class ApiKeyAdminService
{
    private readonly SqliteApiKeyStore _store;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _currentUser;

    public ApiKeyAdminService(SqliteApiKeyStore store, SqliteAuditLog audit, CurrentUserAccessor currentUser)
    {
        _store = store;
        _audit = audit;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken ct) => _store.ListAsync("wineventlog", ct);

    /// <summary>Returns the plaintext key exactly once — the caller must show it now.</summary>
    public async Task<(string Label, string PlaintextKey)> CreateAsync(string label, string? allowedSourceIp, CancellationToken ct)
    {
        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        string key = SqliteApiKeyStore.GenerateKey();
        await _store.CreateAsync(label, key, "wineventlog", string.IsNullOrWhiteSpace(allowedSourceIp) ? null : allowedSourceIp,
            me.UserName, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.SecretChange, me.UserName, "api_key", label, Detail: "Windows Event Log API key created"),
            CancellationToken.None).ConfigureAwait(false);
        return (label, key);
    }

    public async Task RevokeAsync(long id, CancellationToken ct)
    {
        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        await _store.RevokeAsync(id, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.SecretChange, me.UserName, "api_key", id.ToString(), Detail: "API key revoked"),
            CancellationToken.None).ConfigureAwait(false);
    }
}
