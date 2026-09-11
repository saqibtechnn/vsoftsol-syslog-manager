using System.Security.Cryptography.X509Certificates;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Resolves the server certificate for <see cref="TlsSyslogListener"/>. A delegate rather
/// than a seam (ADR 0008/0014/0015 precedent): only the composition root can reach both the
/// Windows certificate store and the Data-layer secret store that holds a PFX password, so
/// it builds the actual resolution logic and hands the listener this delegate.
/// </summary>
public delegate ValueTask<X509Certificate2?> TlsCertificateProvider(CancellationToken cancellationToken);
