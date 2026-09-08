using Microsoft.Extensions.Logging;
using VSoftSol.Syslog.Data.Streams;
using VSoftSol.Syslog.Rules.Streams;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Owns the current <see cref="StreamRouter"/> for the ingest path and rebuilds it when
/// <see cref="SqliteStreamStore.Version"/> changes (an operator edited a stream). Lives in
/// the composition root — the only layer allowed to bridge <c>Data</c> (the store) and
/// <c>Rules</c> (the router). One process hosts both the collector and the UI (ADR 0005),
/// so the in-memory version counter is authoritative. A stream whose rule will not compile
/// is logged once per rebuild and left out, never blocking the rest.
/// </summary>
public sealed class StreamRouterProvider : IDisposable
{
    private readonly SqliteStreamStore _store;
    private readonly ILogger<StreamRouterProvider> _logger;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);

    private volatile StreamRouter? _router;
    private long _builtVersion = -1;

    public StreamRouterProvider(SqliteStreamStore store, ILogger<StreamRouterProvider> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<StreamRouter> GetAsync(CancellationToken cancellationToken)
    {
        StreamRouter? current = _router;
        if (current is not null && Interlocked.Read(ref _builtVersion) == _store.Version)
        {
            return current;
        }

        await _rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long version = _store.Version;
            if (_router is not null && _builtVersion == version)
            {
                return _router;
            }

            IReadOnlyList<StreamRow> rows = await _store.ListActiveAsync(cancellationToken).ConfigureAwait(false);
            StreamRouter built = StreamRouter.Build(rows.Select(r =>
                new StreamDefinition(r.StreamId, r.Name, r.Enabled, r.IsCatchAll, r.Match)));

            foreach (StreamCompileError error in built.CompileErrors)
            {
                _logger.LogWarning(
                    "Stream '{Name}' (#{Id}) has an invalid match rule and is not routing: {Errors}",
                    error.Name, error.StreamId, string.Join("; ", error.Errors));
            }

            _router = built;
            Interlocked.Exchange(ref _builtVersion, version);
            return built;
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    public void Dispose() => _rebuildGate.Dispose();
}
