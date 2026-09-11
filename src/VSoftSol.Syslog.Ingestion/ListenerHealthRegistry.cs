using System.Collections.Concurrent;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Which listeners are currently up, published by <see cref="IngestionHostedService"/> as
/// it starts/stops each one. This is the "listener down" self-monitoring signal
/// (PHASE_11 item 6) — a listener enabled in configuration but absent here means its
/// <c>StartAsync</c> never completed (or it was later stopped) while the collector kept
/// running.
/// </summary>
public sealed class ListenerHealthRegistry
{
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);

    public void MarkRunning(string listenerName) => _running[listenerName] = true;

    public void MarkStopped(string listenerName) => _running[listenerName] = false;

    public IReadOnlyDictionary<string, bool> Snapshot() => _running;
}
