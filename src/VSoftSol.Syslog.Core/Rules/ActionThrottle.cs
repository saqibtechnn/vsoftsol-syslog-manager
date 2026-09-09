namespace VSoftSol.Syslog.Core.Rules;

/// <summary>
/// Per-action rate limit and cool-down (PHASE_07 item 3 — "each with per-action rate limit
/// and cool-down"). A pure value; the runtime enforces it with a token bucket and a
/// virtual clock.
/// </summary>
/// <param name="MaxPerWindow">
/// The most times this action may run per <see cref="WindowSeconds"/>. 0 = unlimited.
/// </param>
/// <param name="WindowSeconds">The sliding window for <see cref="MaxPerWindow"/>. Ignored when it is 0.</param>
/// <param name="CooldownSeconds">
/// Minimum gap between two runs of this action <em>for the same rule</em>. 0 = none.
/// </param>
public readonly record struct ActionThrottle(int MaxPerWindow, int WindowSeconds, int CooldownSeconds)
{
    /// <summary>No rate limit and no cool-down.</summary>
    public static ActionThrottle None => new(0, 0, 0);

    /// <summary>True when a rate limit is configured.</summary>
    public bool HasRateLimit => MaxPerWindow > 0 && WindowSeconds > 0;

    /// <summary>True when a cool-down is configured.</summary>
    public bool HasCooldown => CooldownSeconds > 0;
}
