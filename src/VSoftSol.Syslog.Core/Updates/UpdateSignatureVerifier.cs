using VSoftSol.Syslog.Core.Bundles;

namespace VSoftSol.Syslog.Core.Updates;

/// <summary>
/// Verifies an update manifest's signature against the one public key baked into this
/// build (v1.1 — ADR 0021). It is structurally impossible for any caller to substitute a
/// different key — <see cref="ReleaseSigningInfo.PublicKeyBase64"/> is a compile-time
/// constant, not a parameter. Deliberately not trust-on-first-use (contrast with
/// <c>ConfigBundleImporter</c>'s TOFU model for config bundles, ADR 0019): the only
/// acceptable signer for a self-update is the vendor's own key, decided once, not whichever
/// key happens to arrive with the first manifest a compromised channel could serve.
/// </summary>
public static class UpdateSignatureVerifier
{
    /// <summary>False whenever no real signing key was configured at build time
    /// (<see cref="ReleaseSigningInfo.HasRealKey"/>) — fails closed as "not configured,"
    /// never treats the placeholder key as a real trust anchor.</summary>
    public static bool Verify(string documentJson, string signatureBase64) =>
        ReleaseSigningInfo.HasRealKey
        && BundleSigner.Verify(documentJson, signatureBase64, ReleaseSigningInfo.PublicKeyBase64);
}
