using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Anchor type for assembly-scoped tests and DI scanning. Phase 0 shell — listeners,
/// the bounded channel, and the disk spill queue arrive in Phase 2.
/// </summary>
public static class IngestionAssemblyMarker
{
    /// <summary>The Core type this layer produces.</summary>
    public static Type Produces => typeof(SyslogEvent);
}
