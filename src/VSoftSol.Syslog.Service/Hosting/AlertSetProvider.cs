using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Rules.Alerts;
using VSoftSol.Syslog.Rules.Rules;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Owns the current compiled <see cref="CompiledAlertSet"/> for the scheduler and rebuilds
/// it when <see cref="SqliteAlertStore.Version"/> changes (an operator edited an alert).
/// Mirrors <see cref="RuleSetProvider"/> — only the composition root may bridge <c>Data</c>
/// (the store) and <c>Rules</c> (the compiler). An alert that will not compile is logged
/// once per rebuild and left out.
/// </summary>
public sealed class AlertSetProvider : IDisposable
{
    private readonly SqliteAlertStore _store;
    private readonly ILogger<AlertSetProvider> _logger;
    private readonly RuleCompileOptions _compileOptions;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);

    private volatile CompiledAlertSet? _set;
    private long _builtVersion = -1;

    public AlertSetProvider(
        SqliteAlertStore store,
        ILogger<AlertSetProvider> logger,
        IOptions<Rules.Actions.ActionExecutorOptions>? actionOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Rules.Actions.ActionExecutorOptions a = actionOptions?.Value ?? new();
        _compileOptions = new RuleCompileOptions(
            a.ScriptAllowListDirectories,
            a.FileActionBaseDirectory,
            a.LocalSyslogEndpoints);
    }

    public async ValueTask<CompiledAlertSet> GetAsync(CancellationToken cancellationToken)
    {
        CompiledAlertSet? current = _set;
        if (current is not null && Interlocked.Read(ref _builtVersion) == _store.Version)
        {
            return current;
        }

        await _rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long version = _store.Version;
            if (_set is not null && _builtVersion == version)
            {
                return _set;
            }

            IReadOnlyList<AlertRow> rows = await _store.ListActiveAsync(cancellationToken).ConfigureAwait(false);
            CompiledAlertSet built = new AlertCompiler(_compileOptions).CompileSet(rows.Select(r => r.ToDefinition()));

            foreach (AlertCompileError error in built.CompileErrors)
            {
                _logger.LogWarning(
                    "Alert '{Name}' (#{Id}) has an invalid definition and is not evaluating: {Errors}",
                    error.Name, error.AlertId, string.Join("; ", error.Errors));
            }

            _set = built;
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
