using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Rules.Conditions;

namespace VSoftSol.Syslog.Rules.Streams;

/// <summary>A stream and its (unparsed) match rule, as loaded from the store.</summary>
public sealed record StreamDefinition(long StreamId, string Name, bool Enabled, bool IsCatchAll, ConditionNode? Match);

/// <summary>One stream whose rule failed to compile — surfaced to the operator, never fatal.</summary>
public sealed record StreamCompileError(long StreamId, string Name, IReadOnlyList<string> Errors);

/// <summary>
/// Evaluates every enabled stream's match rule against an event, once, at ingest
/// (PHASE_06 build item 6). The catch-all stream is always included. Each stream is
/// isolated: a rule that fails to compile is dropped (reported via
/// <see cref="CompileErrors"/>); a regex that times out at evaluation makes only that
/// stream not match — ingestion and the other streams are unaffected.
/// </summary>
public sealed class StreamRouter
{
    private readonly IReadOnlyList<long> _catchAllIds;
    private readonly IReadOnlyList<CompiledStream> _streams;

    private StreamRouter(IReadOnlyList<long> catchAllIds, IReadOnlyList<CompiledStream> streams, IReadOnlyList<StreamCompileError> errors)
    {
        _catchAllIds = catchAllIds;
        _streams = streams;
        CompileErrors = errors;
    }

    /// <summary>Streams whose rule could not be compiled (bad regex, unknown field, …).</summary>
    public IReadOnlyList<StreamCompileError> CompileErrors { get; }

    /// <summary>The count of enabled, routable streams (catch-all + compiled rules).</summary>
    public int StreamCount => _catchAllIds.Count + _streams.Count;

    public static StreamRouter Build(IEnumerable<StreamDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var compiler = new ConditionCompiler();
        var catchAll = new List<long>();
        var compiled = new List<CompiledStream>();
        var errors = new List<StreamCompileError>();

        foreach (StreamDefinition def in definitions)
        {
            if (!def.Enabled)
            {
                continue;
            }

            if (def.IsCatchAll)
            {
                catchAll.Add(def.StreamId);
                continue;
            }

            ConditionCompileResult result = compiler.Compile(def.Match);
            if (result.Success)
            {
                compiled.Add(new CompiledStream(def.StreamId, result.Condition!));
            }
            else
            {
                errors.Add(new StreamCompileError(def.StreamId, def.Name, result.Errors));
            }
        }

        return new StreamRouter(catchAll, compiled, errors);
    }

    /// <summary>The ids of every stream the event belongs to, catch-all first.</summary>
    public IReadOnlyList<long> Route(SyslogEvent syslogEvent)
    {
        ArgumentNullException.ThrowIfNull(syslogEvent);

        var ids = new List<long>(_catchAllIds.Count + 2);
        ids.AddRange(_catchAllIds);

        foreach (CompiledStream stream in _streams)
        {
            try
            {
                if (ConditionEvaluator.Matches(stream.Condition, syslogEvent))
                {
                    ids.Add(stream.StreamId);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A stream rule can never take down routing for the others or for ingestion.
            }
        }

        return ids;
    }

    private sealed record CompiledStream(long StreamId, CompiledCondition Condition);
}
