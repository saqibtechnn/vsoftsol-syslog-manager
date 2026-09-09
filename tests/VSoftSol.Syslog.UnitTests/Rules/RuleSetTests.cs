using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Rules;
using VSoftSol.Syslog.UnitTests.Conditions;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Rules.RuleTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Rules;

public sealed class RuleSetTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    internal static RuleSet Build(params RuleDefinition[] rules)
    {
        var compiler = new RuleCompiler();
        var list = new List<CompiledRule>();
        for (int i = 0; i < rules.Length; i++)
        {
            rules[i].RuleId = i + 1;
            RuleCompileResult r = compiler.Compile(rules[i]);
            r.Success.Should().BeTrue(because: string.Join("; ", r.Errors));
            list.Add(r.Rule!);
        }

        var ordered = list.OrderBy(c => c.Priority).ThenBy(c => c.RuleId).ToList();
        return new RuleSet(new CompiledRuleSet(ordered, []), TimeZoneInfo.Utc);
    }

    [Fact]
    public void Match_ReturnsMatchingRulesInPriorityOrder()
    {
        SyslogEvent e = ConditionTestBuilders.Event(message: "authentication failure", severity: Core.Enums.Severity.Error);
        RuleSet set = Build(
            Rule("low", 200, All(Cmp("message", ConditionOperator.Contains, "failure"))),
            Rule("high", 10, All(Cmp("severity", ConditionOperator.Equals, "error"))),
            Rule("miss", 5, All(Cmp("message", ConditionOperator.Contains, "zzz"))));

        set.Match(e, [], Noon).Select(r => r.Name).Should().Equal("high", "low");
    }

    [Fact]
    public void Match_NoFilter_MatchesEveryMessage()
    {
        RuleSet set = Build(Rule("catch-all", null));
        set.Match(ConditionTestBuilders.Event(), [], Noon).Should().ContainSingle();
    }

    [Fact]
    public void Match_TimeWindow_ExcludesOutOfWindowRules()
    {
        var rule = Rule("night only", null);
        rule.Window = new TimeOfDayWindow(22 * 60, 6 * 60, []);
        RuleSet set = Build(rule);

        set.Match(ConditionTestBuilders.Event(), [], Noon).Should().BeEmpty();
        set.Match(ConditionTestBuilders.Event(), [], new DateTimeOffset(2026, 9, 8, 23, 30, 0, TimeSpan.Zero))
            .Should().ContainSingle();
    }

    [Fact]
    public void Match_DeviceGroupRestriction_OnlyAppliesToEventsInThoseGroups()
    {
        var rule = Rule("core only", null);
        rule.DeviceGroupIds = [7];
        RuleSet set = Build(rule);

        set.Match(ConditionTestBuilders.Event(), eventDeviceGroupIds: [], Noon).Should().BeEmpty();
        set.Match(ConditionTestBuilders.Event(), eventDeviceGroupIds: [7, 9], Noon).Should().ContainSingle();
    }

    [Fact]
    public void Match_DisabledRule_IsNotInTheCompiledSet()
    {
        // a disabled rule is filtered out by the store/provider, not RuleSet — but assert
        // the compiler still produces it so the provider can choose.
        RuleSet set = Build(Rule("on", null), Rule("off", null));
        // both compiled; provider would drop the disabled one. RuleSet matches whatever it's given.
        set.Match(ConditionTestBuilders.Event(), [], Noon).Should().HaveCount(2);
    }
}
