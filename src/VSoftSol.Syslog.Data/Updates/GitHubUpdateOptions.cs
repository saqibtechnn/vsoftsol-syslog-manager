namespace VSoftSol.Syslog.Data.Updates;

/// <summary>
/// Bootstrap-tier, non-admin-editable config for the self-update GitHub client (v1.1 — ADR
/// 0021) — compiled-in product facts (which repo, which asset filenames), not a live
/// setting. Contrast with <c>UpdateSettings</c> (the DB-backed enabled/interval toggle,
/// genuinely Administrator-editable). <see cref="AllowedPrivateNetworkCidrs"/> exists only
/// so a test can point this client at a local loopback fixture — nothing in the Web UI ever
/// sets it, and it is empty (no override) in every real deployment.
/// </summary>
public sealed record GitHubUpdateOptions
{
    public string ApiUrl { get; init; } = "https://api.github.com/repos/saqibtechnn/vsoftsol-syslog-manager/releases/latest";

    public string ManifestAssetName { get; init; } = "update-manifest.json";

    public string MsiAssetNameSuffix { get; init; } = ".msi";

    /// <summary>Hosts a manifest/MSI download (including one followed redirect) may resolve
    /// to. GitHub Releases assets are served via a redirect to a CDN — these are the only
    /// two hosts that redirect is ever followed to.</summary>
    public IReadOnlyList<string> AllowedAssetHosts { get; init; } = ["github.com", "objects.githubusercontent.com"];

    /// <summary>Test-only escape hatch for <c>PrivateNetworkGuard</c> — see the type doc.
    /// Always empty in production.</summary>
    public IReadOnlyList<string> AllowedPrivateNetworkCidrs { get; init; } = [];

    /// <summary>True in production (manifest/MSI downloads must be https — GitHub always
    /// serves over TLS and there is no legitimate reason to accept plain http for a
    /// security-critical update channel). Test-only escape hatch, alongside
    /// <see cref="AllowedPrivateNetworkCidrs"/> — this codebase's local test fixture
    /// (<c>HttpSink</c>) only serves plain http, and standing up a self-signed local HTTPS
    /// listener has no existing precedent in this test suite worth inventing here.</summary>
    public bool RequireHttps { get; init; } = true;

    public int ConnectTimeoutSeconds { get; init; } = 10;

    public int RequestTimeoutSeconds { get; init; } = 30;
}
