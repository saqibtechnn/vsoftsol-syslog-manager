using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Rules.Conditions;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Conditions.ConditionTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Conditions;

/// <summary>
/// The shared condition engine (PHASE_06 streams; PHASE_07/08 reuse it). Locks the
/// operator semantics; the routing-oracle integration test then proves the stream router
/// agrees with an independent evaluator over thousands of generated cases.
/// </summary>
public sealed class ConditionEvaluatorTests
{
    private static bool Match(ConditionNode tree, SyslogEvent e)
    {
        ConditionCompileResult compiled = new ConditionCompiler().Compile(tree);
        compiled.Success.Should().BeTrue(because: string.Join("; ", compiled.Errors));
        return ConditionEvaluator.Matches(compiled.Condition!, e);
    }

    [Theory]
    [InlineData("message", ConditionOperator.Contains, "failed", true)]
    [InlineData("message", ConditionOperator.Contains, "FAILED", true)]
    [InlineData("message", ConditionOperator.Contains, "success", false)]
    [InlineData("message", ConditionOperator.Matches, "fail(ed|ure)", true)]
    [InlineData("message", ConditionOperator.StartsWith, "login", true)]
    [InlineData("message", ConditionOperator.EndsWith, "root", true)]
    [InlineData("hostname", ConditionOperator.Equals, "core-sw-1", true)]
    [InlineData("host", ConditionOperator.StartsWith, "core", true)]
    [InlineData("hostname", ConditionOperator.NotEquals, "core-sw-1", false)]
    [InlineData("source_ip", ConditionOperator.Equals, "10.0.0.9", true)]
    [InlineData("app", ConditionOperator.Equals, "sshd", true)]
    [InlineData("vendor", ConditionOperator.Equals, "cisco-ios", true)]
    [InlineData("protocol", ConditionOperator.Equals, "udp", true)]
    [InlineData("parse_status", ConditionOperator.Equals, "rfc3164", true)]
    public void Comparison_TextOperators(string field, ConditionOperator op, string value, bool expected) =>
        Match(Cmp(field, op, value), Event()).Should().Be(expected);

    [Theory]
    [InlineData("severity", ConditionOperator.Equals, "warning", true)]
    [InlineData("severity", ConditionOperator.Equals, "4", true)]
    [InlineData("severity", ConditionOperator.Equals, "error", false)]
    [InlineData("severity", ConditionOperator.GreaterThan, "3", true)]  // warning code 4 > 3
    [InlineData("severity", ConditionOperator.LessThan, "3", false)]
    [InlineData("facility", ConditionOperator.Equals, "local7", true)]
    [InlineData("facility", ConditionOperator.Equals, "23", true)]
    [InlineData("occurrence_count", ConditionOperator.GreaterThan, "1", false)]
    public void Comparison_SeverityFacilityNumeric(string field, ConditionOperator op, string value, bool expected) =>
        Match(Cmp(field, op, value), Event()).Should().Be(expected);

    [Fact]
    public void Comparison_OccurrenceCount_Numeric() =>
        Match(Cmp("occurrence_count", ConditionOperator.GreaterThan, "2"), Event(occurrenceCount: 3)).Should().BeTrue();

    [Fact]
    public void Comparison_NotEquals_OnAbsentField_IsTrue() =>
        Match(Cmp("app", ConditionOperator.NotEquals, "sshd"), Event(app: null)).Should().BeTrue();

    [Theory]
    [InlineData("field.action", ConditionOperator.Equals, "deny", true)]
    [InlineData("field.action", ConditionOperator.Equals, "allow", false)]
    [InlineData("field.srcport", ConditionOperator.GreaterThan, "20", true)]
    [InlineData("field.missing", ConditionOperator.Exists, "", false)]
    [InlineData("field.missing", ConditionOperator.NotExists, "", true)]
    [InlineData("field.action", ConditionOperator.Exists, "", true)]
    public void Comparison_ExtractedFields(string field, ConditionOperator op, string value, bool expected) =>
        Match(Cmp(field, op, value),
            Event(fields: [new EventField("action", "deny"), new EventField("srcport", "22")])).Should().Be(expected);

    [Fact]
    public void Comparison_InList() =>
        Match(Cmp("hostname", ConditionOperator.InList, "core-sw-1, core-sw-2\nedge-fw-1"), Event()).Should().BeTrue();

    [Theory]
    [InlineData("core-sw-1", "warning", true)]
    [InlineData("core-sw-1", "error", false)]
    [InlineData("other", "warning", false)]
    public void Group_And(string host, string sev, bool expected) =>
        Match(All(Cmp("hostname", ConditionOperator.Equals, host), Cmp("severity", ConditionOperator.Equals, sev)), Event())
            .Should().Be(expected);

    [Theory]
    [InlineData("other", "warning", true)]
    [InlineData("other", "error", false)]
    public void Group_Or(string host, string sev, bool expected) =>
        Match(Any(Cmp("hostname", ConditionOperator.Equals, host), Cmp("severity", ConditionOperator.Equals, sev)), Event())
            .Should().Be(expected);

    [Fact]
    public void Group_Nested()
    {
        ConditionGroup tree = All(
            Any(Cmp("hostname", ConditionOperator.Equals, "nope"), Cmp("vendor", ConditionOperator.Equals, "cisco-ios")),
            Cmp("message", ConditionOperator.Contains, "failed"));

        Match(tree, Event()).Should().BeTrue();
    }

    [Fact]
    public void EmptyGroup_MatchesNothing()
    {
        ConditionCompileResult compiled = new ConditionCompiler().Compile(new ConditionGroup());
        compiled.Success.Should().BeTrue();
        ConditionEvaluator.Matches(compiled.Condition!, Event()).Should().BeFalse();
    }

    [Fact]
    public void NullCondition_MatchesNothing()
    {
        ConditionCompileResult compiled = new ConditionCompiler().Compile(null);
        compiled.Success.Should().BeTrue();
        ConditionEvaluator.Matches(compiled.Condition!, Event()).Should().BeFalse();
    }
}
