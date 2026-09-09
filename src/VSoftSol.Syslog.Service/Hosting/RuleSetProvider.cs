using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.Rules.Rules;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Owns the current compiled <see cref="RuleSet"/> for the ingest path and rebuilds it when
/// <see cref="SqliteRuleStore.Version"/> changes (an operator edited a rule). Mirrors
/// <see cref="StreamRouterProvider"/>: only the composition root may bridge <c>Data</c> (the
/// store) and <c>Rules</c> (the engine), and ADR 0005 (one process) makes the in-memory
/// version counter authoritative. A rule whose filter/actions will not compile is logged
/// once per rebuild and left out, never blocking the rest.
/// </summary>
public sealed class RuleSetProvider : IDisposable
{
    private readonly SqliteRuleStore _store;
    private readonly ILogger<RuleSetProvider> _logger;
    private readonly RuleCompileOptions _compileOptions;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);

    private volatile RuleSet? _ruleSet;
    private long _builtVersion = -1;

    public RuleSetProvider(
        SqliteRuleStore store,
        ILogger<RuleSetProvider> logger,
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

    public async ValueTask<RuleSet> GetAsync(CancellationToken cancellationToken)
    {
        RuleSet? current = _ruleSet;
        if (current is not null && Interlocked.Read(ref _builtVersion) == _store.Version)
        {
            return current;
        }

        await _rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long version = _store.Version;
            if (_ruleSet is not null && _builtVersion == version)
            {
                return _ruleSet;
            }

            IReadOnlyList<RuleRow> rows = await _store.ListActiveAsync(cancellationToken).ConfigureAwait(false);
            CompiledRuleSet compiled = new RuleCompiler(_compileOptions).CompileSet(rows.Select(r => r.ToDefinition()));
            var built = new RuleSet(compiled);

            foreach (RuleCompileError error in compiled.CompileErrors)
            {
                _logger.LogWarning(
                    "Rule '{Name}' (#{Id}) has an invalid definition and is not evaluating: {Errors}",
                    error.Name, error.RuleId, string.Join("; ", error.Errors));
            }

            _ruleSet = built;
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
