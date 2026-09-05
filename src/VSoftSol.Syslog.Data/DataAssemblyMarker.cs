using VSoftSol.Syslog.Core.Abstractions;

namespace VSoftSol.Syslog.Data;

/// <summary>
/// Anchor type for assembly-scoped tests and DI scanning. Phase 0 shell — real
/// repositories, migrations, and FTS5 wiring arrive in Phase 1.
/// </summary>
public static class DataAssemblyMarker
{
    /// <summary>The Core seam this layer will implement.</summary>
    public static Type ImplementsSeam => typeof(ILogRepository);
}
