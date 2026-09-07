using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace VSoftSol.Syslog.Data.Secrets;

/// <summary>
/// DPAPI-backed <see cref="ISecretProtector"/> (SECURITY_STANDARDS.md §5.5). Secrets are
/// encrypted to the service account (<see cref="DataProtectionScope.CurrentUser"/>) with a
/// fixed application entropy value, so the ciphertext is useless if the database file is
/// copied to another host or opened by another account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Application-wide additional entropy. Not a secret itself — it binds the ciphertext to
    // this product so another DPAPI-using app on the same box cannot round-trip our blobs.
    private static readonly byte[] Entropy =
        [0x56, 0x53, 0x6F, 0x66, 0x74, 0x53, 0x6F, 0x6C, 0x2E, 0x53, 0x79, 0x73, 0x6C, 0x6F, 0x67, 0x21];

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}
