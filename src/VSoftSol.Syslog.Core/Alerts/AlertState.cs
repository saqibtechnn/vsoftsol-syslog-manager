namespace VSoftSol.Syslog.Core.Alerts;

/// <summary>
/// The lifecycle state of an <see cref="AlertInstance"/> (PHASE_08 build item 5):
/// <c>Firing → Acknowledged → Resolved</c>. A resolved instance is closed; a new breach
/// opens a fresh instance.
/// </summary>
public enum AlertState
{
    /// <summary>The condition is (or was) breached and nobody has acknowledged it.</summary>
    Firing,

    /// <summary>An operator has acknowledged the alert; it stays open until resolved.</summary>
    Acknowledged,

    /// <summary>Closed — resolved by an operator, or auto-resolved when the condition cleared.</summary>
    Resolved,
}
