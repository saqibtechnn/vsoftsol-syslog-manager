using System.Security.Claims;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Updates;

/// <summary>Self-update check settings and the "already-verified, ready to install" download
/// (v1.1 — ADR 0021). Administrator-only, matching every other Settings page.</summary>
public sealed class UpdateAdminService
{
    private readonly SqliteUpdateSettingsStore _store;
    private readonly UpdateChecker _checker;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;

    public UpdateAdminService(SqliteUpdateSettingsStore store, UpdateChecker checker, SqliteAuditLog audit, CurrentUserAccessor users)
    {
        _store = store;
        _checker = checker;
        _audit = audit;
        _users = users;
    }

    public Task<UpdateSettings> GetStatusAsync(CancellationToken ct) => _store.GetAsync(ct);

    public async Task<(bool Ok, string Message)> SaveEnabledStateAsync(bool enabled, int checkIntervalHours, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        await _store.SaveEnabledStateAsync(enabled, checkIntervalHours, user.UserName, ct).ConfigureAwait(false);
        await _audit.AppendAsync(
            new AuditEntry(AuditActions.UpdateSettingsChange, user.UserName, "update_settings", null,
                Detail: $"check_enabled={enabled}, check_interval_hours={checkIntervalHours}"),
            CancellationToken.None).ConfigureAwait(false);
        return (true, enabled ? "Update checks enabled." : "Update checks disabled.");
    }

    /// <summary>The manual "Check now" button — runs immediately rather than waiting for the
    /// next scheduled tick.</summary>
    public async Task<(bool Ok, string Message)> CheckNowAsync(CancellationToken ct)
    {
        await _checker.RunOnceAsync(ct).ConfigureAwait(false);
        UpdateSettings settings = await _store.GetAsync(ct).ConfigureAwait(false);
        if (settings.UpdateReady)
        {
            return (true, $"Version {settings.LatestKnownVersion} is ready to install.");
        }

        return settings.LastCheckError is { Length: > 0 } error
            ? (false, $"Check failed: {error}")
            : (true, "Up to date.");
    }

    /// <summary>Re-verifies the staged MSI's hash immediately before streaming it —
    /// defense-in-depth against the file changing on disk between the check and the
    /// download, the same discipline this codebase already applies before restoring an
    /// archive. Returns null if there is nothing ready, or if the on-disk file no longer
    /// matches what was recorded.
    ///
    /// <para>Takes the caller's <see cref="ClaimsPrincipal"/> directly rather than going
    /// through <see cref="CurrentUserAccessor"/>: this method is called from a plain minimal
    /// API endpoint (<c>UpdateEndpoints</c>), not from within a Razor component's circuit —
    /// <c>CurrentUserAccessor</c> wraps Blazor Server's own
    /// <c>ServerAuthenticationStateProvider</c>, which throws
    /// <c>InvalidOperationException</c> ("Do not call GetAuthenticationStateAsync outside of
    /// the DI scope for a Razor component") when resolved from a request that never went
    /// through the component circuit. <c>SaveEnabledStateAsync</c>/<c>CheckNowAsync</c> above
    /// are both called from <c>UpdatesSettingsPage.razor</c>'s own circuit, so they keep using
    /// <see cref="CurrentUserAccessor"/> — only this endpoint-invoked method needed the
    /// change.</para></summary>
    public async Task<(Stream Content, string FileName)?> GetVerifiedDownloadAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        UpdateSettings settings = await _store.GetAsync(ct).ConfigureAwait(false);
        if (!settings.UpdateReady || settings.DownloadedMsiPath is null || settings.DownloadedMsiSha256 is null)
        {
            return null;
        }

        if (!File.Exists(settings.DownloadedMsiPath))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(settings.DownloadedMsiPath, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        string actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, settings.DownloadedMsiSha256, StringComparison.Ordinal))
        {
            return null;
        }

        var user = new CurrentUser(caller);
        await _audit.AppendAsync(
            new AuditEntry(AuditActions.UpdateMsiDownloadedByAdmin, user.UserName, "update_settings", null,
                Detail: $"version {settings.LatestKnownVersion}"),
            CancellationToken.None).ConfigureAwait(false);

        string fileName = $"VSoftSolSyslogManagerSetup-{settings.LatestKnownVersion}.msi";
        return (new MemoryStream(bytes), fileName);
    }
}
