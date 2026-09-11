namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Validates a Windows Event Log intake request's API key (and, where the key is scoped to
/// an expected source, the request's source IP — "unable to be used to forge events
/// attributed to another host" per SECURITY_STANDARDS.md). A delegate for the same reason
/// as <see cref="TlsCertificateProvider"/> and <see cref="SnmpCommunityProvider"/>: only the
/// composition root can reach the Data-layer key store.
/// </summary>
public delegate ValueTask<bool> WinEventLogApiKeyValidator(string apiKey, string sourceIp, CancellationToken cancellationToken);
