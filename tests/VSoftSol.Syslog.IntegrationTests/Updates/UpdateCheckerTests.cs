using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Core.Updates;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

/// <summary>
/// <see cref="UpdateChecker"/> orchestration (v1.1 — ADR 0021). Two scenarios are honestly
/// testable in this dev checkout: "disabled" and "no release-signing key configured" — both
/// gate before any network call, and both reflect this repo's *actual* current state
/// (<see cref="ReleaseSigningInfo.HasRealKey"/> is false; no real keypair has been
/// generated). Every code path past that gate (a manifest that verifies against the real
/// key, a tampered one that fails it, the full download-and-stage-a-real-update flow) is —
/// by design, the same design property that makes the trust anchor secure — only
/// exercisable with a real production signing key, which does not and should not exist in
/// this repository or this sandbox. That is not a gap in this test file; it is the intended
/// consequence of "the only acceptable signer is a key the vendor generates and holds
/// themselves" (release-signing/README.md). The plan's own Verification section already
/// calls for a manual end-to-end check with a real signed test release before this feature
/// is considered done — the same disposition as every other "needs real infrastructure this
/// sandbox lacks" item already carried in this project's history.
/// </summary>
[Trait("Category", "Updates")]
public sealed class UpdateCheckerTests
{
    private static UpdateChecker Checker(SqliteTestDatabase db, GitHubUpdateClient client, out List<(string Level, string Title)> notifications)
    {
        var recorded = new List<(string, string)>();
        notifications = recorded;
        var settings = new SqliteUpdateSettingsStore(db.Factory);
        var audit = new SqliteAuditLog(db.Factory);
        NotificationSink sink = (level, title, _, _, _) =>
        {
            recorded.Add((level.ToString(), title));
            return ValueTask.CompletedTask;
        };

        return new UpdateChecker(settings, client, audit, sink, currentVersion: "1.1.0", dataDirectory: Path.GetTempPath());
    }

    [Fact]
    public async Task RunOnceAsync_Disabled_MakesNoNetworkCall_RecordsNothing()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using var sink = new HttpSink(async ctx => await ctx.Response.OutputStream.WriteAsync("{}"u8.ToArray()));
        var client = new GitHubUpdateClient(new HttpClient(), Options.Create(new GitHubUpdateOptions
        {
            ApiUrl = $"http://127.0.0.1:{sink.Port}/api",
            RequireHttps = false,
        }));

        UpdateChecker checker = Checker(db, client, out List<(string Level, string Title)> notifications);
        // update_settings is seeded disabled by migration 011 — no explicit save needed.
        await checker.RunOnceAsync(CancellationToken.None);

        sink.RequestCount.Should().Be(0, "a disabled checker must never make an outbound call");
        notifications.Should().BeEmpty();
        (await new SqliteUpdateSettingsStore(db.Factory).GetAsync(CancellationToken.None)).LastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task RunOnceAsync_Enabled_NoReleaseSigningKeyConfigured_RecordsAClearError_MakesNoNetworkCall()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var settingsStore = new SqliteUpdateSettingsStore(db.Factory);
        await settingsStore.SaveEnabledStateAsync(enabled: true, checkIntervalHours: 24, "admin", CancellationToken.None);

        await using var sink = new HttpSink(async ctx => await ctx.Response.OutputStream.WriteAsync("{}"u8.ToArray()));
        var client = new GitHubUpdateClient(new HttpClient(), Options.Create(new GitHubUpdateOptions
        {
            ApiUrl = $"http://127.0.0.1:{sink.Port}/api",
            RequireHttps = false,
        }));

        UpdateChecker checker = Checker(db, client, out List<(string Level, string Title)> notifications);
        await checker.RunOnceAsync(CancellationToken.None);

        ReleaseSigningInfo.HasRealKey.Should().BeFalse("this checkout has no real release-signing key configured");
        sink.RequestCount.Should().Be(0, "there is nothing to verify a manifest against, so checking GitHub at all would be pointless");
        notifications.Should().BeEmpty();

        VSoftSol.Syslog.Data.Updates.UpdateSettings settings = await settingsStore.GetAsync(CancellationToken.None);
        settings.LastCheckError.Should().NotBeNullOrEmpty();
        settings.LastCheckError.Should().ContainEquivalentOf("no release-signing key");
        settings.UpdateReady.Should().BeFalse();
    }

    // ---- The full verify-and-stage path, against a throwaway test keypair -------------------
    // Production builds can only ever trust the vendor key baked in at compile time; the checker's
    // internal constructor lets a test supply its own public key so the path past the key gate
    // (verify → compare versions → download → hash-check → stage) is exercised for real.

    private static readonly byte[] MsiBytes = "a pretend installer payload"u8.ToArray();

    private sealed record Fixture(
        SqliteTestDatabase Db, HttpSink Sink, UpdateChecker Checker, SqliteUpdateSettingsStore Settings,
        List<(string Level, string Title)> Notifications, string DataDir) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Sink.DisposeAsync();
            await Db.DisposeAsync();
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static string ManifestJson(string version, byte[] msi) =>
        JsonSerializer.Serialize(new UpdateManifest(
            FormatVersion: 1,
            Version: version,
            MsiSha256: Convert.ToHexString(SHA256.HashData(msi)).ToLowerInvariant(),
            MsiUrl: "https://github.com/example/releases/download/v" + version + "/setup.msi",
            ReleaseNotesUrl: "https://github.com/example/releases/tag/v" + version,
            PublishedUtc: DateTimeOffset.UtcNow));

    /// <summary>Starts a fake release server plus a checker that trusts <paramref name="trustedPublicKey"/>.</summary>
    /// <param name="documentJson">The manifest document the server publishes.</param>
    /// <param name="signedDocumentJson">What the signature is actually computed over (differs from
    /// <paramref name="documentJson"/> to simulate tampering).</param>
    private static async Task<Fixture> StartAsync(
        string documentJson, string signedDocumentJson, string signerPrivateKey, string trustedPublicKey, byte[] servedMsi)
    {
        string signature = BundleSigner.Sign(signedDocumentJson, signerPrivateKey);
        string signedManifest = JsonSerializer.Serialize(new SignedUpdateManifest(documentJson, signature));
        string version = JsonDocument.Parse(documentJson).RootElement.GetProperty("Version").GetString()!;

        var sink = new HttpSink(async ctx =>
        {
            int port = ctx.Request.LocalEndPoint.Port;
            string path = ctx.Request.Url!.AbsolutePath;
            byte[] body = path switch
            {
                "/api" => Encoding.UTF8.GetBytes(
                    $$"""{"tag_name":"v{{version}}","assets":[{"name":"update-manifest.json","browser_download_url":"http://127.0.0.1:{{port}}/manifest"},{"name":"setup.msi","browser_download_url":"http://127.0.0.1:{{port}}/setup.msi"}]}"""),
                "/manifest" => Encoding.UTF8.GetBytes(signedManifest),
                "/setup.msi" => servedMsi,
                _ => [],
            };
            await ctx.Response.OutputStream.WriteAsync(body);
        });

        SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var settings = new SqliteUpdateSettingsStore(db.Factory);
        await settings.SaveEnabledStateAsync(enabled: true, checkIntervalHours: 24, "admin", CancellationToken.None);

        var client = new GitHubUpdateClient(new HttpClient(), Options.Create(new GitHubUpdateOptions
        {
            ApiUrl = $"http://127.0.0.1:{sink.Port}/api",
            AllowedAssetHosts = ["127.0.0.1"],
            AllowedPrivateNetworkCidrs = ["127.0.0.1/32"],
            RequireHttps = false,
            RequestTimeoutSeconds = 5,
        }));

        var recorded = new List<(string Level, string Title)>();
        NotificationSink notify = (level, title, _, _, _) =>
        {
            recorded.Add((level.ToString(), title));
            return ValueTask.CompletedTask;
        };

        string dataDir = Path.Combine(Path.GetTempPath(), "update-checker-" + Guid.NewGuid().ToString("N"));
        var checker = new UpdateChecker(
            settings, client, new SqliteAuditLog(db.Factory), notify,
            currentVersion: "1.1.0", dataDirectory: dataDir, releasePublicKeyBase64: trustedPublicKey);
        return new Fixture(db, sink, checker, settings, recorded, dataDir);
    }

    [Fact]
    public async Task RunOnceAsync_ValidlySignedNewerRelease_DownloadsVerifiesAndStagesTheInstaller()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string document = ManifestJson("9.9.9", MsiBytes);
        await using Fixture f = await StartAsync(document, document, privateKey, publicKey, MsiBytes);

        await f.Checker.RunOnceAsync(CancellationToken.None);

        UpdateSettings settings = await f.Settings.GetAsync(CancellationToken.None);
        settings.UpdateReady.Should().BeTrue(settings.LastCheckError);
        settings.LastCheckError.Should().BeNull();
        settings.DownloadedMsiPath.Should().NotBeNull();
        File.ReadAllBytes(settings.DownloadedMsiPath!).Should().Equal(MsiBytes);
        f.Notifications.Should().ContainSingle(n => n.Title.StartsWith("Update ready"));
    }

    [Fact]
    public async Task RunOnceAsync_ManifestTamperedAfterSigning_IsRejected_AndTheInstallerIsNeverDownloaded()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string signedByVendor = ManifestJson("9.9.9", MsiBytes);
        string servedByAttacker = ManifestJson("9.9.9", "a different, malicious installer"u8.ToArray());
        await using Fixture f = await StartAsync(servedByAttacker, signedByVendor, privateKey, publicKey, MsiBytes);

        await f.Checker.RunOnceAsync(CancellationToken.None);

        UpdateSettings settings = await f.Settings.GetAsync(CancellationToken.None);
        settings.UpdateReady.Should().BeFalse();
        settings.LastCheckError.Should().ContainEquivalentOf("signature");
        f.Sink.RequestCount.Should().Be(2, "only the release lookup and the manifest were fetched — never the .msi");
        f.Notifications.Should().ContainSingle(n => n.Title.Contains("signature verification failed"));
        Directory.Exists(f.DataDir).Should().BeFalse("nothing was staged");
    }

    [Fact]
    public async Task RunOnceAsync_ManifestSignedByAnUntrustedKey_IsRejected()
    {
        (string trustedPublicKey, _) = BundleSigner.GenerateKeyPair();
        (_, string attackerPrivateKey) = BundleSigner.GenerateKeyPair();
        string document = ManifestJson("9.9.9", MsiBytes);
        await using Fixture f = await StartAsync(document, document, attackerPrivateKey, trustedPublicKey, MsiBytes);

        await f.Checker.RunOnceAsync(CancellationToken.None);

        (await f.Settings.GetAsync(CancellationToken.None)).UpdateReady.Should().BeFalse();
        f.Sink.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task RunOnceAsync_InstallerDoesNotMatchTheSignedHash_IsNotStaged()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string document = ManifestJson("9.9.9", MsiBytes);
        await using Fixture f = await StartAsync(document, document, privateKey, publicKey, "swapped installer bytes"u8.ToArray());

        await f.Checker.RunOnceAsync(CancellationToken.None);

        UpdateSettings settings = await f.Settings.GetAsync(CancellationToken.None);
        settings.UpdateReady.Should().BeFalse();
        settings.LastCheckError.Should().ContainEquivalentOf("hash-verify");
        Directory.EnumerateFiles(f.DataDir, "*", SearchOption.AllDirectories).Should().BeEmpty("a mismatching download leaves no file behind");
    }

    [Fact]
    public async Task RunOnceAsync_ReleaseIsNotNewerThanTheRunningVersion_ReportsUpToDate_WithoutDownloading()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string document = ManifestJson("1.1.0", MsiBytes);
        await using Fixture f = await StartAsync(document, document, privateKey, publicKey, MsiBytes);

        await f.Checker.RunOnceAsync(CancellationToken.None);

        UpdateSettings settings = await f.Settings.GetAsync(CancellationToken.None);
        settings.UpdateReady.Should().BeFalse();
        settings.LastCheckError.Should().BeNull();
        settings.LastCheckedUtc.Should().NotBeNull();
        f.Sink.RequestCount.Should().Be(2);
    }
}
