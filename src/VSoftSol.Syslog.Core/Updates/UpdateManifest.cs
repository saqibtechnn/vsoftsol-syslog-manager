namespace VSoftSol.Syslog.Core.Updates;

/// <summary>
/// A signed, versioned pointer to one published release (v1.1 — ADR 0021). Deliberately
/// does not carry the MSI's bytes or a raw binary signature — the release-signing tool signs
/// this small document instead, and the product re-hashes the actual downloaded MSI against
/// <see cref="MsiSha256"/> as a second, independent integrity check.
/// </summary>
public sealed record UpdateManifest(
    int FormatVersion,
    string Version,
    string MsiSha256,
    string MsiUrl,
    string ReleaseNotesUrl,
    DateTimeOffset PublishedUtc);

/// <summary>
/// The wire form: the manifest's canonical JSON text plus an ECDSA P-256 signature over
/// those exact bytes. Unlike <c>SignedBundle</c> (config bundles, ADR 0019's
/// trust-on-first-use model), this carries no public key or fingerprint of its own — the
/// only signer self-update ever trusts is the one key baked into this build
/// (<see cref="ReleaseSigningInfo"/>). A manifest that shipped its own key would let a
/// forged manifest "verify" against itself, which would prove nothing.
/// </summary>
public sealed record SignedUpdateManifest(string DocumentJson, string SignatureBase64);
