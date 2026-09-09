using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Rules.Conditions;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>
/// The compiled rule set, evaluated once per message at ingest (PHASE_07 item 2). Pure and
/// allocation-light: <see cref="Match"/> returns the rules whose filter, time window, and
/// device-group scope all admit the event, in priority order. It does <b>not</b> decide
/// rate limits, cool-downs, escalation, or the <c>Suppress</c> halt — those are runtime
/// state and belong to <see cref="RuleRuntime"/>.
/// </summary>
public sealed class RuleSet
{
    private readonly CompiledRuleSet _compiled;
    private readonly TimeZoneInfo _localZone;

    public RuleSet(CompiledRuleSet compiled, TimeZoneInfo? localZone = null)
    {
        _compiled = compiled ?? throw new ArgumentNullException(nameof(compiled));
        _localZone = localZone ?? TimeZoneInfo.Local;
    }

    public IReadOnlyList<RuleCompileError> CompileErrors => _compiled.CompileErrors;

    public int RuleCount => _compiled.RuleCount;

    /// <summary>
    /// The rules that admit <paramref name="syslogEvent"/> right now, priority order.
    /// <paramref name="utcNow"/> drives the time-of-day window (converted to the collector's
    /// local zone). <paramref name="eventDeviceGroupIds"/> is the set of device-group ids the
    /// event's device belongs to (resolved by the composition root); a rule restricted to
    /// groups the event is not in does not apply.
    /// </summary>
    public IReadOnlyList<CompiledRule> Match(
        SyslogEvent syslogEvent, IReadOnlyList<long> eventDeviceGroupIds, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(syslogEvent);
        ArgumentNullException.ThrowIfNull(eventDeviceGroupIds);
        if (_compiled.Rules.Count == 0)
        {
            return [];
        }

        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(utcNow, _localZone);
        var matched = new List<CompiledRule>();

        foreach (CompiledRule rule in _compiled.Rules)
        {
            if (rule.Window is { } window && !window.Contains(localNow))
            {
                continue;
            }

            if (rule.DeviceGroupIds.Count > 0 && !rule.DeviceGroupIds.Any(eventDeviceGroupIds.Contains))
            {
                continue;
            }

            if (!rule.AlwaysMatches && !ConditionEvaluator.Matches(rule.Filter, syslogEvent))
            {
                continue;
            }

            matched.Add(rule);
        }

        return matched;
    }
}
