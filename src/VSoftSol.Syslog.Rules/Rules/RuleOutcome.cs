using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>One side-effecting action that passed throttling and is to be enqueued to the outbox.</summary>
public sealed record PendingDispatch(
    long RuleId,
    string RuleName,
    int ActionIndex,
    RuleAction Action,
    bool WasEscalation);

/// <summary>
/// A count of side-effecting actions the global per-minute budget collapsed instead of
/// dispatching (PHASE_07 item 5). The engine emits <b>one</b> summary notification per
/// minute carrying this.
/// </summary>
public sealed record StormSummary(int TotalCollapsed, IReadOnlyDictionary<string, int> ByKind);

/// <summary>
/// The result of running the matched rules for one event through
/// <see cref="RuleRuntime.Apply"/>: the inline mutations to apply to the event before it is
/// stored, and the side-effecting actions to enqueue.
/// </summary>
public sealed record RuleOutcome
{
    public static RuleOutcome Empty { get; } = new();

    /// <summary>Tags to add to the event (<c>event_fields</c> rows) before commit.</summary>
    public IReadOnlyList<EventField> Tags { get; init; } = [];

    /// <summary>Extra stream ids to route the event to, on top of the Phase 6 routing result.</summary>
    public IReadOnlyList<long> ExtraStreamIds { get; init; } = [];

    /// <summary>True when a <c>Suppress</c> action halted the rule chain for this event.</summary>
    public bool Suppressed { get; init; }

    /// <summary>Side-effecting actions to write to the outbox in the event's transaction.</summary>
    public IReadOnlyList<PendingDispatch> Dispatches { get; init; } = [];

    /// <summary>How many side-effecting actions were dropped by a per-action rate limit / cool-down.</summary>
    public int RateLimitedCount { get; init; }

    /// <summary>Set when the global budget collapsed dispatches into a summary this call.</summary>
    public StormSummary? Storm { get; init; }

    public bool HasWork =>
        Tags.Count > 0 || ExtraStreamIds.Count > 0 || Dispatches.Count > 0 || Storm is not null;
}
