using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Rules.Rules;
using VSoftSol.Syslog.Service.Hosting;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Builds the Phase 7 ingest enricher (rule evaluation → outbox) over a test database,
/// mirroring what <c>SyslogPlatformExtensions.AddCollectorRuntime</c> wires in production
/// (minus the device resolver — group-restricted rules are covered separately).
/// </summary>
internal static class RuleEnricherFactory
{
    public static (EventEnricher Enricher, RuleHitTracker Hits, SqliteRuleStore Store) Build(
        SqliteTestDatabase db, TimeProvider? time = null)
    {
        var store = new SqliteRuleStore(db.Factory);
        var provider = new RuleSetProvider(store, NullLogger<RuleSetProvider>.Instance);
        var runtime = new RuleRuntime(time ?? TimeProvider.System, new RuleRuntimeOptions { GlobalActionsPerMinute = 0 });
        var hits = new RuleHitTracker();
        TimeProvider clock = time ?? TimeProvider.System;

        EventEnricher enricher = async (parsed, ct) =>
        {
            RuleSet ruleSet = await provider.GetAsync(ct).ConfigureAwait(false);
            if (ruleSet.RuleCount == 0)
            {
                return parsed;
            }

            DateTimeOffset now = clock.GetUtcNow();
            IReadOnlyList<CompiledRule> matched = ruleSet.Match(parsed, [], now);
            if (matched.Count == 0)
            {
                return parsed;
            }

            foreach (CompiledRule rule in matched)
            {
                hits.Record(rule.RuleId, now);
            }

            RuleOutcome outcome = runtime.Apply(matched, parsed);
            if (!outcome.HasWork)
            {
                return parsed;
            }

            var pending = outcome.Dispatches.Select(d => new PendingRuleAction(
                d.RuleId, d.RuleName, d.ActionIndex,
                RuleActionInfo.Kind(d.Action), RuleJson.SerializeAction(d.Action), d.WasEscalation)).ToList();

            return parsed.WithRuleOutcome(outcome.Tags, outcome.ExtraStreamIds, pending);
        };

        return (enricher, hits, store);
    }
}
