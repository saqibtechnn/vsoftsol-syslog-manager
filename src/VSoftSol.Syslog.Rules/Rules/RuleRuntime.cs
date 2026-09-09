using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>Tunable limits for <see cref="RuleRuntime"/> (bound from the <c>Rules</c> config section).</summary>
public sealed class RuleRuntimeOptions
{
    public const string SectionName = "Rules";

    /// <summary>Global cap on side-effecting actions dispatched per minute (PHASE_07 item 5). 0 disables.</summary>
    public int GlobalActionsPerMinute { get; set; } = 300;

    /// <summary>The <c>event_fields</c> name an <see cref="AddTagAction"/> writes to.</summary>
    public string TagFieldName { get; set; } = "tag";
}

/// <summary>
/// The stateful half of the rules engine (PHASE_07 items 3–5): per-action rate limits and
/// cool-downs, per-rule escalation windows, and the global alert-storm budget. Driven by an
/// injected <see cref="TimeProvider"/> so every time-dependent test uses a virtual clock
/// (TESTING_STANDARDS §2.2). In-memory and process-local — the counters reset on restart,
/// which is safe: message-level idempotency is the outbox's job (ADR 0015), not this.
/// </summary>
public sealed class RuleRuntime
{
    private readonly TimeProvider _time;
    private readonly RuleRuntimeOptions _options;
    private readonly object _gate = new();

    private readonly Dictionary<(long Rule, string Action), SlidingWindow> _rateWindows = [];
    private readonly Dictionary<(long Rule, string Action), DateTimeOffset> _cooldownUntil = [];
    private readonly Dictionary<long, EscalationState> _escalation = [];
    private readonly SlidingWindow _globalBudget = new();
    private DateTimeOffset _stormSummaryClearedAt = DateTimeOffset.MinValue;

    public RuleRuntime(TimeProvider? timeProvider = null, RuleRuntimeOptions? options = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new RuleRuntimeOptions();
    }

    /// <summary>
    /// Runs the matched rules (priority order, from <see cref="RuleSet.Match"/>) for one
    /// event: chooses the normal or escalation action list per rule, applies inline actions
    /// to the outcome, throttles the side-effecting ones, and enforces the storm budget.
    /// </summary>
    public RuleOutcome Apply(IReadOnlyList<CompiledRule> matched, SyslogEvent syslogEvent)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(syslogEvent);
        if (matched.Count == 0)
        {
            return RuleOutcome.Empty;
        }

        DateTimeOffset now = _time.GetUtcNow();
        var tags = new List<EventField>();
        var streams = new List<long>();
        var dispatches = new List<PendingDispatch>();
        bool suppressed = false;
        int rateLimited = 0;
        var stormByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        int stormTotal = 0;

        lock (_gate)
        {
            foreach (CompiledRule rule in matched)
            {
                (IReadOnlyList<RuleAction> list, bool escalated) = ChooseList(rule, now);

                bool halt = false;
                for (int i = 0; i < list.Count; i++)
                {
                    RuleAction action = list[i];
                    switch (action)
                    {
                        case SuppressAction:
                            suppressed = true;
                            halt = true;
                            break;

                        case AddTagAction tag:
                            string value = Sanitize(FieldTemplate.Render(tag.Tag, syslogEvent));
                            if (value.Length > 0)
                            {
                                tags.Add(new EventField(_options.TagFieldName, value));
                            }

                            break;

                        case RouteToStreamAction route when route.StreamId > 0:
                            if (!streams.Contains(route.StreamId))
                            {
                                streams.Add(route.StreamId);
                            }

                            break;

                        case var _ when RuleActionInfo.IsSideEffecting(action):
                            DispatchDecision decision = Throttle(rule.RuleId, action, now);
                            if (decision == DispatchDecision.Allow)
                            {
                                dispatches.Add(new PendingDispatch(rule.RuleId, rule.Name, i, action, escalated));
                            }
                            else if (decision == DispatchDecision.RateLimited)
                            {
                                rateLimited++;
                            }
                            else
                            {
                                string label = RuleActionInfo.Label(action);
                                stormTotal++;
                                stormByKind[label] = stormByKind.GetValueOrDefault(label) + 1;
                            }

                            break;
                    }

                    if (halt)
                    {
                        break;
                    }
                }

                if (halt)
                {
                    break;
                }
            }

            StormSummary? storm = null;
            if (stormTotal > 0 && ShouldEmitStormSummary(now))
            {
                storm = new StormSummary(stormTotal, stormByKind);
            }

            return new RuleOutcome
            {
                Tags = tags,
                ExtraStreamIds = streams,
                Suppressed = suppressed,
                Dispatches = dispatches,
                RateLimitedCount = rateLimited,
                Storm = storm,
            };
        }
    }

    private (IReadOnlyList<RuleAction> List, bool Escalated) ChooseList(CompiledRule rule, DateTimeOffset now)
    {
        if (rule.Escalation is not { } policy)
        {
            return (rule.Actions, false);
        }

        EscalationState state = _escalation.TryGetValue(rule.RuleId, out EscalationState? existing)
            ? existing
            : _escalation[rule.RuleId] = new EscalationState();

        var window = TimeSpan.FromSeconds(policy.WindowSeconds);
        state.Matches.Enqueue(now);
        while (state.Matches.Count > 0 && now - state.Matches.Peek() > window)
        {
            state.Matches.Dequeue();
        }

        bool overThreshold = state.Matches.Count >= policy.Threshold;
        bool latchOpen = now - state.LastEscalatedAt > window;

        if (overThreshold && latchOpen)
        {
            state.LastEscalatedAt = now;
            return (policy.EscalationActions, true);
        }

        return (rule.Actions, false);
    }

    private DispatchDecision Throttle(long ruleId, RuleAction action, DateTimeOffset now)
    {
        ActionThrottle throttle = action.Throttle;
        var key = (ruleId, action.Id);

        if (throttle.HasCooldown
            && _cooldownUntil.TryGetValue(key, out DateTimeOffset until)
            && now < until)
        {
            return DispatchDecision.RateLimited;
        }

        if (throttle.HasRateLimit)
        {
            SlidingWindow rw = _rateWindows.TryGetValue(key, out SlidingWindow? existing)
                ? existing
                : _rateWindows[key] = new SlidingWindow();
            rw.Trim(now, TimeSpan.FromSeconds(throttle.WindowSeconds));
            if (rw.Count >= throttle.MaxPerWindow)
            {
                return DispatchDecision.RateLimited;
            }
        }

        if (_options.GlobalActionsPerMinute > 0)
        {
            _globalBudget.Trim(now, TimeSpan.FromMinutes(1));
            if (_globalBudget.Count >= _options.GlobalActionsPerMinute)
            {
                return DispatchDecision.StormCollapsed;
            }
        }

        // Commit the decision.
        if (throttle.HasRateLimit)
        {
            _rateWindows[key].Add(now);
        }

        if (throttle.HasCooldown)
        {
            _cooldownUntil[key] = now + TimeSpan.FromSeconds(throttle.CooldownSeconds);
        }

        if (_options.GlobalActionsPerMinute > 0)
        {
            _globalBudget.Add(now);
        }

        return DispatchDecision.Allow;
    }

    private bool ShouldEmitStormSummary(DateTimeOffset now)
    {
        if (now - _stormSummaryClearedAt < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        _stormSummaryClearedAt = now;
        return true;
    }

    private static string Sanitize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(Math.Min(value.Length, 128));
        foreach (char c in value)
        {
            if (sb.Length == 128)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private enum DispatchDecision
    {
        Allow,
        RateLimited,
        StormCollapsed,
    }

    private sealed class EscalationState
    {
        public Queue<DateTimeOffset> Matches { get; } = new();

        public DateTimeOffset LastEscalatedAt { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class SlidingWindow
    {
        private readonly Queue<DateTimeOffset> _stamps = new();

        public int Count => _stamps.Count;

        public void Trim(DateTimeOffset now, TimeSpan window)
        {
            while (_stamps.Count > 0 && now - _stamps.Peek() >= window)
            {
                _stamps.Dequeue();
            }
        }

        public void Add(DateTimeOffset now) => _stamps.Enqueue(now);
    }
}
