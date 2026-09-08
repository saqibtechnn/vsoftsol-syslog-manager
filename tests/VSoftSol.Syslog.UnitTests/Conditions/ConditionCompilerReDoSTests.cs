using System.Diagnostics;
using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Rules.Conditions;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Conditions.ConditionTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Conditions;

/// <summary>
/// PHASE_06 "Regex-rule safety" + ReDoS suite: a user-authored catastrophic-backtracking
/// pattern must be rejected or run in bounded time, and a malicious stream rule cannot
/// stall ingestion. Stream regexes compile with <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/>
/// (linear time — ReDoS impossible by construction) plus a 250 ms match timeout.
/// </summary>
public sealed class ConditionCompilerReDoSTests
{
    [Theory]
    // Classic catastrophic-backtracking patterns. All compile AND evaluate in bounded time
    // against an input that would hang a backtracking engine.
    [InlineData("(a+)+$")]
    [InlineData("(a|a)*$")]
    [InlineData("(a*)*$")]
    [InlineData("(.*a){20}$")]
    [InlineData("(x+x+)+y")]
    public void HostilePattern_CompilesAndEvaluatesInBoundedTime(string pattern)
    {
        ConditionCompileResult compiled = new ConditionCompiler().Compile(
            Cmp("message", ConditionOperator.Matches, pattern));
        compiled.Success.Should().BeTrue(because: string.Join("; ", compiled.Errors));

        // The input that would blow up a backtracking engine.
        var evil = Event(message: new string('a', 60) + "!" + new string('x', 60));

        Func<bool> act = () => ConditionEvaluator.Matches(compiled.Condition!, evil);

        var sw = Stopwatch.StartNew();
        act.Should().NotThrow();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "NonBacktracking guarantees linear time; the 250 ms match timeout is a further backstop");
    }

    [Theory]
    [InlineData(@"(\w+)\s\1", "linear-time")]   // backreference — not supported by NonBacktracking
    [InlineData("(?<=foo)bar", "linear-time")]  // lookbehind
    [InlineData("(?=foo)bar", "linear-time")]   // lookahead
    [InlineData("(", "")]                        // just invalid
    [InlineData("a{99999999,}", "")]             // absurd quantifier → huge automaton
    public void UnsupportedOrInvalidPattern_IsRejected(string pattern, string fragment)
    {
        ConditionCompileResult compiled = new ConditionCompiler().Compile(
            Cmp("message", ConditionOperator.Matches, pattern));

        compiled.Success.Should().BeFalse();
        compiled.Errors.Should().NotBeEmpty();
        if (fragment.Length > 0)
        {
            string.Join(" ", compiled.Errors).Should().ContainEquivalentOf(fragment);
        }
    }

    [Fact]
    public void ManyRegexRules_EachIsolated_OneBadPatternDoesNotBreakTheRest()
    {
        ConditionGroup tree = Any(
            Cmp("message", ConditionOperator.Matches, "(a+)+$"),
            Cmp("message", ConditionOperator.Matches, "failed"),
            Cmp("message", ConditionOperator.Matches, "(x*)*y"));

        ConditionCompileResult compiled = new ConditionCompiler().Compile(tree);
        compiled.Success.Should().BeTrue();

        ConditionEvaluator.Matches(compiled.Condition!, Event()).Should().BeTrue();
    }
}
