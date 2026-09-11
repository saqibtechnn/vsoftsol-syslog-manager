namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Resolves the currently-accepted SNMP community string(s) from the secret store. A
/// delegate (ADR 0008/0014/0015 precedent) so Ingestion never references the Data-layer
/// secret store directly; resolved fresh per received trap so a rotated community takes
/// effect immediately, without restarting the listener.
/// </summary>
public delegate ValueTask<IReadOnlyList<string>> SnmpCommunityProvider(CancellationToken cancellationToken);
