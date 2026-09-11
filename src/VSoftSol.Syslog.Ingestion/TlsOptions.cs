using System.ComponentModel.DataAnnotations;
using System.Security.Authentication;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// TLS syslog listener tuning (PHASE_11 item 1). Off by default — a listener that needs a
/// certificate configured before it can bind must never silently start with none.
/// </summary>
public sealed class TlsOptions
{
    public const string SectionName = "Tls";

    public bool Enabled { get; set; }

    [Required]
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>6514 is the IANA-assigned syslog-over-TLS port (RFC 5425).</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 6514;

    /// <summary>Certificate source 1: a thumbprint looked up in the Windows certificate
    /// store (LocalMachine\My). Preferred — no private key touches disk outside the store.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Certificate source 2: a PFX file path. <see cref="PfxPasswordSecretName"/>
    /// names the DPAPI-protected secret holding its password (never stored in the clear).</summary>
    public string? PfxPath { get; set; }

    public string? PfxPasswordSecretName { get; set; }

    public SslProtocols MinimumProtocol { get; set; } = SslProtocols.Tls12;

    /// <summary>Mutual TLS: require and validate a client certificate.</summary>
    public bool RequireClientCertificate { get; set; }

    /// <summary>Client certificate thumbprints allowed when
    /// <see cref="RequireClientCertificate"/> is set. An unknown or removed (operator's
    /// revocation mechanism for v1: delete the thumbprint here) client is rejected.</summary>
    public List<string> TrustedClientCertificateThumbprints { get; set; } = [];

    [Range(1, 100_000)]
    public int MaxConnections { get; set; } = 1_000;

    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
