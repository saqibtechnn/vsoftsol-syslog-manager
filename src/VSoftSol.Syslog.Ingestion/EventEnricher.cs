using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// A hook the composition root supplies to attach ingest-time device resolution and stream
/// routing to a parsed event (PHASE_06; ADR 0014). It is a delegate, not a seam interface:
/// the pipeline neither knows nor cares that it bridges the <c>Data</c> and <c>Rules</c>
/// layers (which the architecture forbids the pipeline from referencing directly). A null
/// enricher leaves the event unattributed and unrouted, which is a valid Phase-2 state.
/// </summary>
public delegate ValueTask<SyslogEvent> EventEnricher(SyslogEvent parsed, CancellationToken cancellationToken);
