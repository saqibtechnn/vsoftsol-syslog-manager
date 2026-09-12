using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace VSoftSol.Syslog.Web.Hosting;

/// <summary>
/// Generates the Web UI's HTTPS certificate on first run (PHASE_12 build item 1: "Generates
/// a self-signed HTTPS certificate at install with a documented path to replacing it with a
/// real one"). Runs before dependency injection exists (Kestrel needs the certificate to
/// bind its HTTPS endpoint), so it uses the same bootstrap-phase Serilog static logger
/// <c>Program.cs</c> already sets up for this exact reason, and takes the data directory
/// directly rather than through <c>IOptions</c>.
/// </summary>
public static class WebCertificateProvisioning
{
    private const string FileName = "web-https.pfx";

    /// <summary>
    /// Loads the existing certificate at <c>&lt;dataDirectory&gt;/certs/web-https.pfx</c>,
    /// or generates and persists a new self-signed one if none exists yet. The private key
    /// is protected by the data directory's NTFS ACLs (service account only, set by the
    /// installer) rather than a PFX password — there is no secret store to draw a password
    /// from this early in startup, before the database (and therefore the secret store) is
    /// even guaranteed to exist.
    /// </summary>
    public static X509Certificate2 EnsureCertificate(string dataDirectory)
    {
        string certDirectory = Path.Combine(dataDirectory, "certs");
        Directory.CreateDirectory(certDirectory);
        string certPath = Path.Combine(certDirectory, FileName);

        if (File.Exists(certPath))
        {
            try
            {
                return LoadFromDisk(certPath);
            }
            catch (CryptographicException ex)
            {
                Log.Warning(ex, "The existing HTTPS certificate at {Path} could not be loaded; generating a replacement self-signed certificate.", certPath);
            }
        }

        X509Certificate2 generated = GenerateSelfSigned();
        File.WriteAllBytes(certPath, generated.Export(X509ContentType.Pfx));
        Log.Information(
            "Generated a self-signed HTTPS certificate at {Path} (2-year validity, subject {Subject}). " +
            "Replace it with a certificate from your organisation's CA by placing a PFX file at this exact " +
            "path and restarting the service — see the Admin Guide, \"Replacing the HTTPS certificate\".",
            certPath, generated.Subject);

        return LoadFromDisk(certPath);
    }

    private static X509Certificate2 LoadFromDisk(string certPath) =>
        new(certPath, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

    private static X509Certificate2 GenerateSelfSigned()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // Server Authentication

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
