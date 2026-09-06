namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// What a listener does with datagrams from a source that exceeds its per-source rate
/// ceiling. The default is <see cref="Throttle"/> because CLAUDE.md Constraint 3 ("never
/// lose a message") outranks abuse mitigation; <see cref="Drop"/> and
/// <see cref="Quarantine"/> are explicit operator choices for hostile conditions and
/// always increment a visible counter.
/// </summary>
public enum RateLimitBreachBehavior
{
    /// <summary>Apply back-pressure: the frame still gets queued (to memory or spill), the
    /// offending source is just slowed. No message is lost.</summary>
    Throttle = 0,

    /// <summary>Discard frames from the offending source while it is over the ceiling and
    /// count every discard. Loss is intentional and attributed.</summary>
    Drop = 1,

    /// <summary>Discard frames from the offending source for a cooldown window after a
    /// breach (not just while instantaneously over), count them, and surface the source as
    /// quarantined so an operator can allow-list or block it.</summary>
    Quarantine = 2,
}
