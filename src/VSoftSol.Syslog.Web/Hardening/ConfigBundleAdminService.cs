using System.Security.Claims;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Bundles;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Hardening;

/// <summary>Settings → Config bundles (PHASE_11 item 4). Export/import, and the
/// trust-on-first-use signer prompt.</summary>
public sealed class ConfigBundleAdminService
{
    private readonly ConfigBundleExporter _exporter;
    private readonly ConfigBundleImporter _importer;
    private readonly SqliteBundleTrustStore _trust;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _currentUser;

    public ConfigBundleAdminService(
        ConfigBundleExporter exporter, ConfigBundleImporter importer, SqliteBundleTrustStore trust,
        SqliteAuditLog audit, CurrentUserAccessor currentUser)
    {
        _exporter = exporter;
        _importer = importer;
        _trust = trust;
        _audit = audit;
        _currentUser = currentUser;
    }

    public static IReadOnlyList<string> AllSections => BundleSections.All;

    /// <summary>
    /// Takes the caller's <see cref="ClaimsPrincipal"/> directly rather than going through
    /// <see cref="CurrentUserAccessor"/>: this method is called from a plain minimal API
    /// endpoint (<c>BundleEndpoints.ExportAsync</c>), not from within a Razor component's
    /// circuit — <c>CurrentUserAccessor</c> wraps Blazor Server's own
    /// <c>ServerAuthenticationStateProvider</c>, which throws <c>InvalidOperationException</c>
    /// ("Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor
    /// component") when resolved from a request that never went through the component
    /// circuit — confirmed as a live 500 on `/bundles/export` (v1.1, same root cause as ADR
    /// 0021's `UpdateAdminService.GetVerifiedDownloadAsync` fix). <see cref="TrustSignerAsync"/>
    /// and <see cref="ApplyAsync"/> below are both called from the Bundles settings page's own
    /// circuit, so they keep using <see cref="CurrentUserAccessor"/> — only this
    /// endpoint-invoked method needed the change.
    /// </summary>
    public async Task<SignedBundle> ExportAsync(IReadOnlySet<string> sections, string title, ClaimsPrincipal caller, CancellationToken ct)
    {
        var me = new CurrentUser(caller);
        SignedBundle bundle = await _exporter.ExportAsync(sections, BundleKind.CustomerExport, title, me.UserName, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.Export, me.UserName, "config_bundle", title, Detail: string.Join(",", sections)),
            CancellationToken.None).ConfigureAwait(false);
        return bundle;
    }

    /// <summary>Step 1 of import: verify the bundle and report whether its signer is
    /// already trusted, without writing anything.</summary>
    public Task<ConfigBundleImporter.VerifiedBundle?> VerifyAsync(SignedBundle bundle, CancellationToken ct) =>
        _importer.VerifyAndCheckTrustAsync(bundle, ct);

    public async Task TrustSignerAsync(string fingerprint, string publicKey, string label, CancellationToken ct)
    {
        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        await _trust.TrustAsync(fingerprint, publicKey, label, me.UserName, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.ConfigChange, me.UserName, "bundle_signer", fingerprint, Detail: $"Trusted signer '{label}'"),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Step 2: apply the requested sections. Refuses outright — no partial
    /// application — if verification fails or the signer is still untrusted.</summary>
    public async Task<BundleImportResult> ApplyAsync(SignedBundle bundle, IReadOnlySet<string> sections, CancellationToken ct)
    {
        CurrentUser me = await _currentUser.GetAsync().ConfigureAwait(false);
        BundleImportResult result = await _importer.ApplyAsync(bundle, sections, me.UserName, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(
                result.Ok ? AuditActions.ConfigChange : AuditActions.ActionFailed, me.UserName, "config_bundle_import", null,
                Detail: result.Ok ? $"Imported: {string.Join(",", result.RowsAppliedBySection.Select(kv => $"{kv.Key}={kv.Value}"))}" : string.Join("; ", result.Errors)),
            CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public Task<IReadOnlyList<BundleImportRecord>> ListImportsAsync(CancellationToken ct) => _trust.ListImportsAsync(ct);

    public Task<IReadOnlyList<TrustedSigner>> ListTrustedSignersAsync(CancellationToken ct) => _trust.ListTrustedAsync(ct);
}
