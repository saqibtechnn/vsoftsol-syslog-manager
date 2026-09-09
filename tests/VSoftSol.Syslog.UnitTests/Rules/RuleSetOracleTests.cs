using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Rules;
using VSoftSol.Syslog.UnitTests.Conditions;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.UnitTests.Rules;

/// <summary>
/// PHASE_07 rules-evaluator oracle: an independent naive matcher computes which rules should
/// match (priority order, filter + time window + device group) for thousands of generated
/// (rule set, event) pairs; the production <see cref="RuleSet.Match"/> must agree exactly.
/// Compensating evidence for the blocked Stryker run (P3-3).
/// </summary>
public sealed class RuleSetOracleTests(ITestOutputHelper output)
{
    private const int RuleSets = 200;
    private const int EventsPerSet = 50;

    [Fact]
    public void ProductionMatcher_AgreesWithTheNaiveMatcher_ForEveryGeneratedCase()
    {
        var rng = new Random(20260908);
        var compiler = new RuleCompiler();
        int divergences = 0;
        int comparisons = 0;

        for (int s = 0; s < RuleSets; s++)
        {
            List<RuleDefinition> defs = GenerateRules(rng, rng.Next(1, 12));
            var compiled = new List<CompiledRule>();
            for (int i = 0; i < defs.Count; i++)
            {
                defs[i].RuleId = i + 1;
                RuleCompileResult r = compiler.Compile(defs[i]);
                r.Success.Should().BeTrue(because: string.Join("; ", r.Errors));
                compiled.Add(r.Rule!);
            }

            var ordered = compiled.OrderBy(c => c.Priority).ThenBy(c => c.RuleId).ToList();
            var set = new RuleSet(new CompiledRuleSet(ordered, []), TimeZoneInfo.Utc);
            var byId = defs.ToDictionary(d => d.RuleId);

            for (int ev = 0; ev < EventsPerSet; ev++)
            {
                SyslogEvent e = GenerateEvent(rng);
                long[] groups = rng.Next(3) == 0 ? [rng.Next(1, 5)] : [];
                DateTimeOffset now = new(2026, 9, 8, rng.Next(24), rng.Next(60), 0, TimeSpan.Zero);

                var actual = set.Match(e, groups, now).Select(r => r.RuleId).ToList();

                var expected = ordered
                    .Where(r => Admits(byId[r.RuleId], e, groups, now))
                    .Select(r => r.RuleId)
                    .ToList();

                comparisons++;
                if (!actual.SequenceEqual(expected))
                {
                    divergences++;
                    if (divergences <= 5)
                    {
                        output.WriteLine($"set {s} event {ev}: actual [{string.Join(",", actual)}] expected [{string.Join(",", expected)}]");
                    }
                }
            }
        }

        output.WriteLine($"{comparisons} comparisons, {divergences} divergences");
        divergences.Should().Be(0);
    }

    private static bool Admits(RuleDefinition rule, SyslogEvent e, IReadOnlyList<long> eventGroups, DateTimeOffset utcNow)
    {
        if (rule.Window is { } w && !w.Contains(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Utc)))
        {
            return false;
        }

        if (rule.DeviceGroupIds.Count > 0 && !rule.DeviceGroupIds.Any(eventGroups.Contains))
        {
            return false;
        }

        bool always = rule.Filter is null || rule.Filter.Children.Count == 0;
        return always || NaiveConditionMatcher.Matches(rule.Filter, e);
    }

    private static List<RuleDefinition> GenerateRules(Random rng, int count)
    {
        var rules = new List<RuleDefinition>();
        for (int i = 0; i < count; i++)
        {
            var rule = new RuleDefinition
            {
                Name = $"r{i}",
                Priority = rng.Next(1, 300),
                Actions = [new RaiseNotificationAction { Title = "hit" }],
                Filter = rng.Next(4) == 0 ? null : GenerateFilter(rng),
            };

            if (rng.Next(3) == 0)
            {
                int start = rng.Next(0, 1400);
                rule.Window = new TimeOfDayWindow(start, (start + rng.Next(60, 720)) % 1440, []);
            }

            if (rng.Next(4) == 0)
            {
                rule.DeviceGroupIds = [rng.Next(1, 5)];
            }

            rules.Add(rule);
        }

        return rules;
    }

    private static ConditionGroup GenerateFilter(Random rng)
    {
        string[] fields = ["message", "hostname", "severity", "facility", "app", "source_ip"];
        string[] words = ["fail", "down", "denied", "error", "up", "config", "root", "cisco"];
        var children = new List<ConditionNode>();
        int n = rng.Next(1, 4);
        for (int i = 0; i < n; i++)
        {
            string field = fields[rng.Next(fields.Length)];
            ConditionOperator op = field is "severity" or "facility"
                ? ConditionOperator.Equals
                : (ConditionOperator)new[] { ConditionOperator.Contains, ConditionOperator.Equals, ConditionOperator.StartsWith }[rng.Next(3)];
            string value = field switch
            {
                "severity" => rng.Next(8).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "facility" => rng.Next(24).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => words[rng.Next(words.Length)],
            };
            children.Add(new ConditionComparison { Field = field, Operator = op, Value = value });
        }

        return new ConditionGroup { Join = rng.Next(2) == 0 ? ConditionJoin.And : ConditionJoin.Or, Children = children };
    }

    private static SyslogEvent GenerateEvent(Random rng)
    {
        string[] msgs = ["interface down", "authentication failure for root", "config changed", "link up", "access denied", "system healthy"];
        string[] hosts = ["core-sw-1", "edge-fw", "app-01", "cisco-rtr"];
        string m = msgs[rng.Next(msgs.Length)];
        return new SyslogEvent
        {
            ReceivedUtc = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero),
            SourceIp = $"10.0.{rng.Next(255)}.{rng.Next(255)}",
            Hostname = hosts[rng.Next(hosts.Length)],
            AppName = rng.Next(2) == 0 ? "sshd" : "kernel",
            Severity = (Severity)rng.Next(8),
            Facility = (Facility)rng.Next(24),
            Protocol = Protocol.Udp,
            Message = m,
            RawMessage = System.Text.Encoding.UTF8.GetBytes(m),
            ParseStatus = ParseStatus.Rfc3164,
            DeviceId = rng.Next(1, 20),
        };
    }
}
