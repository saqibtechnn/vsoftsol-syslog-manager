using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Rules;

/// <summary>
/// Anchor type for assembly-scoped tests and DI scanning. Phase 0 shell — the
/// filter→action engine and alert evaluators arrive in Phases 7 and 8.
/// </summary>
public static class RulesAssemblyMarker
{
    /// <summary>The Core type this layer evaluates.</summary>
    public static Type Evaluates => typeof(SyslogEvent);
}
