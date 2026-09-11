using System.Security.Cryptography;
using System.Text;

namespace VSoftSol.Syslog.Core.Bundles;

/// <summary>
/// ECDSA P-256 signing/verification for config bundles (PHASE_11 item 4). In-box
/// <c>System.Security.Cryptography</c> only — no new dependency. Operates on the exact
/// UTF-8 bytes of the bundle's canonical JSON document; the caller (Data layer) owns
/// producing that canonical text.
/// </summary>
public static class BundleSigner
{
    public static (string PublicKeyBase64, string PrivateKeyBase64) GenerateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        byte[] privateKey = key.ExportPkcs8PrivateKey();
        return (Convert.ToBase64String(publicKey), Convert.ToBase64String(privateKey));
    }

    public static string Sign(string documentJson, string privateKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(documentJson);
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        byte[] signature = key.SignData(Encoding.UTF8.GetBytes(documentJson), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    /// <summary>Never throws on a malformed key or signature — a corrupt input is simply
    /// not a valid signature (SECURITY_STANDARDS.md: fail closed, never a 500).</summary>
    public static bool Verify(string documentJson, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            byte[] signature = Convert.FromBase64String(signatureBase64);
            return key.VerifyData(Encoding.UTF8.GetBytes(documentJson), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A short, human-comparable fingerprint for the TOFU trust prompt —
    /// SHA-256 of the public key, formatted as colon-separated hex groups.</summary>
    public static string Fingerprint(string publicKeyBase64)
    {
        byte[] key = Convert.FromBase64String(publicKeyBase64);
        byte[] hash = SHA256.HashData(key);
        string hex = Convert.ToHexString(hash);
        var groups = new List<string>(hex.Length / 4);
        for (int i = 0; i < hex.Length; i += 4)
        {
            groups.Add(hex.Substring(i, Math.Min(4, hex.Length - i)));
        }

        return string.Join(':', groups);
    }
}
