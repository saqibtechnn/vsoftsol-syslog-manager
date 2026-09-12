using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using VSoftSol.Syslog.Web.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hosting;

/// <summary>PHASE_12 build item 1 — the self-signed HTTPS certificate generated on first run.</summary>
[Trait("Category", "Hosting")]
public sealed class WebCertificateProvisioningTests
{
    private static string NewTempDataDir() => Path.Combine(Path.GetTempPath(), "vsoftsol-certtest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureCertificate_NoExistingFile_GeneratesAUsableServerCertificate()
    {
        string dataDir = NewTempDataDir();
        try
        {
            using X509Certificate2 cert = WebCertificateProvisioning.EnsureCertificate(dataDir);

            cert.HasPrivateKey.Should().BeTrue("Kestrel needs the private key to terminate TLS");
            cert.NotAfter.Should().BeAfter(DateTime.UtcNow.AddMonths(1));
            File.Exists(Path.Combine(dataDir, "certs", "web-https.pfx")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureCertificate_CalledTwice_ReusesTheSamePersistedCertificate()
    {
        string dataDir = NewTempDataDir();
        try
        {
            using X509Certificate2 first = WebCertificateProvisioning.EnsureCertificate(dataDir);
            using X509Certificate2 second = WebCertificateProvisioning.EnsureCertificate(dataDir);

            second.Thumbprint.Should().Be(first.Thumbprint, "a restart must not silently rotate the certificate");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureCertificate_ACorruptedExistingFile_RegeneratesRatherThanFailingStartup()
    {
        string dataDir = NewTempDataDir();
        try
        {
            string certDir = Path.Combine(dataDir, "certs");
            Directory.CreateDirectory(certDir);
            File.WriteAllBytes(Path.Combine(certDir, "web-https.pfx"), [0x00, 0x01, 0x02]); // not a valid PFX

            using X509Certificate2 cert = WebCertificateProvisioning.EnsureCertificate(dataDir);

            cert.HasPrivateKey.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
