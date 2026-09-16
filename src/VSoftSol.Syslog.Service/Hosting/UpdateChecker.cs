using System.Text.Json;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Updates;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Orchestrates one self-update check (v1.1 — ADR 0021): skip if disabled or unconfigured →
/// check the latest release → download and signature-verify its manifest (a failure here
/// stops immediately — the MSI is never downloaded) → compare versions → download and
/// hash-verify the MSI → record the result. Never throws; every failure path is caught,
/// logged via the audit trail, and recorded so the Settings page can show why, matching
/// <c>SelfMonitoringService.TickAsync</c>'s "log and retry next interval" discipline.
/// Lives in <c>Service</c>, not <c>Data</c>, specifically so it can consume
/// <see cref="NotificationSink"/> — a <c>Rules</c>-layer delegate <c>Data</c> is forbidden
/// from referencing (<c>LayeringTests</c>), the same reason
/// <c>SelfMonitoringService</c>/<c>RetentionTieringService</c>/etc. all live here too.
/// </summary>
public sealed class UpdateChecker
{
    private readonly SqliteUpdateSettingsStore _settings;
    private readonly GitHubUpdateClient _client;
    private readonly SqliteAuditLog _audit;
    private readonly NotificationSink _notifications;
    private readonly TimeProvider _time;
    private readonly string _currentVersion;
    private readonly string _dataDirectory;

    public UpdateChecker(
        SqliteUpdateSettingsStore settings,
        GitHubUpdateClient client,
        SqliteAuditLog audit,
        NotificationSink notifications,
        string currentVersion,
        string dataDirectory,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _currentVersion = currentVersion;
        _dataDirectory = dataDirectory;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        UpdateSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.CheckEnabled)
        {
            return;
        }

        if (!ReleaseSigningInfo.HasRealKey)
        {
            // Nothing to verify against — never silently "succeed" against the placeholder key.
            await _settings.RecordCheckResultAsync(_time.GetUtcNow(), "no release-signing key is configured in this build", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();

        GitHubReleaseInfo? release = await _client.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            await _settings.RecordCheckResultAsync(now, "could not reach the update server or parse its response", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(AuditActions.UpdateCheckPerformed, $"checked; latest published tag is {release.Version}", cancellationToken).ConfigureAwait(false);

        if (release.ManifestUrl is null)
        {
            await _settings.RecordCheckResultAsync(now, $"release {release.Version} has no {nameof(GitHubUpdateOptions.ManifestAssetName)} asset", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string? manifestJson = await _client.DownloadManifestAsync(release.ManifestUrl, cancellationToken).ConfigureAwait(false);
        if (manifestJson is null)
        {
            await _settings.RecordCheckResultAsync(now, "could not download the release manifest", cancellationToken).ConfigureAwait(false);
            return;
        }

        Core.Bundles.BundleValidationResult sizeCheck = UpdateManifestValidator.ValidateWireSize(manifestJson);
        if (!sizeCheck.Ok)
        {
            await FailSignatureAsync(release.ManifestUrl, "manifest exceeded the size limit", cancellationToken).ConfigureAwait(false);
            await _settings.RecordCheckResultAsync(now, "release manifest was oversized", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The signature covers the manifest's own document JSON, not a caller-supplied
        // signature parameter — but a signature is delivered alongside the document by the
        // release-signing tool as a SignedUpdateManifest. GitHub serves the manifest asset
        // as that combined JSON object; parse it here rather than as two separate assets.
        SignedUpdateManifest? signed = TryParseSignedManifest(manifestJson);
        if (signed is null)
        {
            await FailSignatureAsync(release.ManifestUrl, "manifest was not a well-formed signed document", cancellationToken).ConfigureAwait(false);
            await _settings.RecordCheckResultAsync(now, "release manifest was malformed", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!UpdateSignatureVerifier.Verify(signed.DocumentJson, signed.SignatureBase64))
        {
            await FailSignatureAsync(release.ManifestUrl, "signature did not verify against the embedded release-signing key", cancellationToken)
                .ConfigureAwait(false);
            await _settings.RecordCheckResultAsync(now, "release manifest signature verification failed", cancellationToken).ConfigureAwait(false);
            return;
        }

        UpdateManifest? manifest = TryParseManifest(signed.DocumentJson);
        Core.Bundles.BundleValidationResult schemaCheck = UpdateManifestValidator.ValidateManifest(manifest);
        if (manifest is null || !schemaCheck.Ok)
        {
            await _settings.RecordCheckResultAsync(now, "release manifest failed schema validation", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ProductVersion.IsNewer(manifest.Version, _currentVersion))
        {
            await _settings.RecordCheckResultAsync(now, error: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (release.MsiUrl is null)
        {
            await _settings.RecordCheckResultAsync(now, $"release {manifest.Version} has no matching .msi asset", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string destinationPath = Path.Combine(_dataDirectory, "updates", manifest.Version, "setup.msi");
        bool downloaded = await _client.DownloadMsiAsync(release.MsiUrl, destinationPath, manifest.MsiSha256, cancellationToken).ConfigureAwait(false);
        if (!downloaded)
        {
            await _settings.RecordCheckResultAsync(now, $"could not download or hash-verify the installer for {manifest.Version}", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _settings.RecordUpdateReadyAsync(now, manifest.Version, signed.DocumentJson, destinationPath, manifest.MsiSha256, cancellationToken)
            .ConfigureAwait(false);
        await AuditAsync(AuditActions.UpdateDownloadedAndVerified, $"version {manifest.Version} downloaded and verified", cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.UpdateAvailable, $"version {manifest.Version} is ready to install", cancellationToken).ConfigureAwait(false);
        await _notifications(
            Core.Rules.NotificationLevel.Info,
            $"Update ready: version {manifest.Version}",
            $"A verified installer for version {manifest.Version} has been downloaded. Visit Settings → Updates to install it.",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FailSignatureAsync(string manifestUrl, string reason, CancellationToken cancellationToken)
    {
        await AuditAsync(AuditActions.UpdateSignatureVerificationFailed, $"{reason} (manifest url: {manifestUrl})", cancellationToken).ConfigureAwait(false);
        await _notifications(
            Core.Rules.NotificationLevel.Warning,
            "Update signature verification failed",
            $"A release manifest could not be verified: {reason}. This may indicate a tampered release or a compromised distribution channel. No installer was downloaded.",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private Task AuditAsync(string action, string detail, CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditEntry(action, Actor: "self-update", EntityType: "update", EntityId: null, Detail: detail),
            cancellationToken);

    private static SignedUpdateManifest? TryParseSignedManifest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedUpdateManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UpdateManifest? TryParseManifest(string documentJson)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(documentJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
