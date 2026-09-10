namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>
/// One lifecycle transition of an <see cref="AlertInstance"/> (PHASE_08 build item 5 —
/// "with actor, timestamp, and free-text note at each transition"). The full ordered list
/// backs the alert-history detail view.
/// </summary>
/// <param name="InstanceId">The instance this transition belongs to.</param>
/// <param name="From">The state before, or null for the opening transition.</param>
/// <param name="To">The state after.</param>
/// <param name="Actor">Who caused it — a user name, or <c>alerts-engine</c> for an automatic transition.</param>
/// <param name="Note">Optional free text recorded with the transition.</param>
/// <param name="OccurredUtc">When it happened.</param>
public sealed record AlertTransition(
    long InstanceId,
    AlertState? From,
    AlertState To,
    string Actor,
    string? Note,
    DateTimeOffset OccurredUtc);
