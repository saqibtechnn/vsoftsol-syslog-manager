using VSoftSol.Syslog.Core.Abstractions;

namespace VSoftSol.Syslog.Reporting;

/// <summary>
/// Anchor type for assembly-scoped tests and DI scanning. Phase 0 shell — retention
/// tiering, Zstd archival, SHA-256 verification, and the report engine arrive in Phase 10.
/// </summary>
public static class ReportingAssemblyMarker
{
    /// <summary>The Core seam this layer reads through.</summary>
    public static Type ReadsThrough => typeof(ILogRepository);
}
