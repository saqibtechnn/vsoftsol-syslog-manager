using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Rules.Alerts;

/// <summary>Tunable limits for <see cref="AlertRuntime"/> (bound from the <c>Alerts</c> config section).</summary>
public sealed class AlertRuntimeOptions
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// Global cap on alert-triggered actions dispatched per minute (PHASE_08 security —
    /// "an attacker who can generate log events must not weaponise alerting against the
    /// admin's inbox"). 0 disables.
    /// </summary>
    public int GlobalActionsPerMinute { get; set; } = 60;
}

/// <summary>What the runtime decided about one queued alert action.</summary>
public enum AlertDispatchDecision
{
    /// <summary>Dispatch it.</summary>
    Allow,

    /// <summary>The action's own rate limit / cool-down blocked it.</summary>
    RateLimited,

    /// <summary>The global per-minute budget is exhausted — collapse to a single summary.</summary>
    StormCollapsed,
}

/// <summary>A summary of the actions the global budget suppressed in the last minute.</summary>
public sealed record AlertStormSummary(int TotalCollapsed, IReadOnlyDictionary<string, int> ByKind);

/// <summary>
/// The stateful guard between an alert firing and its actions being queued (PHASE_08 items
/// 4 &amp; the security section). Per-action rate limit + cool-down (keyed by alert and
/// action id) and a global outbound budget with storm-collapse to one summary notification —
/// mirrors the throttling half of the Phase 7 <c>RuleRuntime</c>. <see cref="TimeProvider"/>
/// driven, in-memory, process-local: the counters reset on restart, which is safe because
/// instance-level dedup (the outbox UNIQUE key) is the store's job, not this.
/// </summary>
public sealed class AlertRuntime
{
    private readonly TimeProvider _time;
    private readonly AlertRuntimeOptions _options;
    private readonly object _gate = new();

    private readonly Dictionary<(long Alert, string Action), SlidingWindow> _rateWindows = [];
    private readonly Dictionary<(long Alert, string Action), DateTimeOffset> _cooldownUntil = [];
    private readonly SlidingWindow _globalBudget = new();
    private readonly Dictionary<string, int> _collapsed = new(StringComparer.Ordinal);
    private int _collapsedTotal;
    private DateTimeOffset _summaryClearedAt = DateTimeOffset.MinValue;

    public AlertRuntime(TimeProvider? timeProvider = null, AlertRuntimeOptions? options = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new AlertRuntimeOptions();
    }

    /// <summary>
    /// Decides whether one alert action may be queued now. On <see cref="AlertDispatchDecision.StormCollapsed"/>
    /// the action is folded into the pending summary (see <see cref="TakeStormSummary"/>).
    /// </summary>
    public AlertDispatchDecision Reserve(long alertId, RuleAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        DateTimeOffset now = _time.GetUtcNow();
        ActionThrottle throttle = action.Throttle;
        var key = (alertId, action.Id);

        lock (_gate)
        {
            if (throttle.HasCooldown
                && _cooldownUntil.TryGetValue(key, out DateTimeOffset until)
                && now < until)
            {
                return AlertDispatchDecision.RateLimited;
            }

            if (throttle.HasRateLimit)
            {
                SlidingWindow rw = _rateWindows.TryGetValue(key, out SlidingWindow? existing)
                    ? existing
                    : _rateWindows[key] = new SlidingWindow();
                rw.Trim(now, TimeSpan.FromSeconds(throttle.WindowSeconds));
                if (rw.Count >= throttle.MaxPerWindow)
                {
                    return AlertDispatchDecision.RateLimited;
                }
            }

            if (_options.GlobalActionsPerMinute > 0)
            {
                _globalBudget.Trim(now, TimeSpan.FromMinutes(1));
                if (_globalBudget.Count >= _options.GlobalActionsPerMinute)
                {
                    string label = RuleActionInfo.Label(action);
                    _collapsed[label] = _collapsed.GetValueOrDefault(label) + 1;
                    _collapsedTotal++;
                    return AlertDispatchDecision.StormCollapsed;
                }
            }

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

            return AlertDispatchDecision.Allow;
        }
    }

    /// <summary>
    /// Returns and clears the pending storm summary, but at most once per minute so the
    /// summary notification itself does not become the flood.
    /// </summary>
    public AlertStormSummary? TakeStormSummary()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_collapsedTotal == 0 || now - _summaryClearedAt < TimeSpan.FromMinutes(1))
            {
                return null;
            }

            var summary = new AlertStormSummary(_collapsedTotal, new Dictionary<string, int>(_collapsed, StringComparer.Ordinal));
            _collapsed.Clear();
            _collapsedTotal = 0;
            _summaryClearedAt = now;
            return summary;
        }
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
