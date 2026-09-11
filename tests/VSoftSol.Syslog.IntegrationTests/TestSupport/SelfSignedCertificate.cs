using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>A throwaway self-signed certificate for TLS listener tests — never a
/// production concern, generated fresh per test with the in-box <see cref="CertificateRequest"/> API.</summary>
internal static class SelfSignedCertificate
{
    public static X509Certificate2 Create(string subjectName = "test-collector")
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Re-import as exportable so SslStream can use the private key across the connection.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
