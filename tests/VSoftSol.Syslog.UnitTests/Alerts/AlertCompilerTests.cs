using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Alerts;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Alerts.AlertTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Alerts;

public sealed class AlertCompilerTests
{
    private static readonly AlertCompiler Compiler = new();

    [Fact]
    public void Compile_AValidThresholdAlert_Succeeds()
    {
        AlertCompileResult result = Compiler.Compile(Threshold(groupBy: "hostname", filter: All(Cmp("message", ConditionOperator.Contains, "failed"))));

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.Alert!.GroupByField.Should().Be("hostname");
        result.Alert.AlwaysMatches.Should().BeFalse();
    }

    [Fact]
    public void Compile_NoName_IsRejected()
    {
        AlertDefinition alert = Threshold();
        alert.Name = "   ";

        Compiler.Compile(alert).Errors.Should().Contain(e => e.Contains("name"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(AlertCompiler.MaxWindowSeconds + 1)]
    public void Compile_WindowOutOfRange_IsRejected(int windowSeconds)
    {
        AlertDefinition alert = Threshold(windowSeconds: windowSeconds);

        Compiler.Compile(alert).Errors.Should().Contain(e => e.Contains("window", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_ZeroInterval_IsRejected()
    {
        AlertDefinition alert = Threshold(intervalSeconds: 0);

        Compiler.Compile(alert).Errors.Should().Contain(e => e.Contains("interval"));
    }

    [Fact]
    public void Compile_ThresholdBelowOne_IsRejected()
    {
        Compiler.Compile(Threshold(threshold: 0)).Errors.Should().Contain(e => e.Contains("threshold", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_DistinctCountWithoutGroupByField_IsRejected()
    {
        var alert = new AlertDefinition
        {
            Name = "spray",
            Type = AlertEvaluationType.DistinctCount,
            Threshold = 10,
        };

        Compiler.Compile(alert).Errors.Should().Contain(e => e.Contains("distinct", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_UnknownGroupByField_IsRejected()
    {
        Compiler.Compile(Threshold(groupBy: "not_a_field")).Errors.Should().Contain(e => e.Contains("group-by", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_GroupByMessageText_IsRejected_WithGuidance()
    {
        Compiler.Compile(Threshold(groupBy: "message")).Errors.Should().Contain(e => e.Contains("bounded set of values"));
    }

    [Fact]
    public void Compile_GroupByExtractedField_Resolves()
    {
        Compiler.Compile(Threshold(groupBy: "field.srcport")).Success.Should().BeTrue();
    }

    [Fact]
    public void Compile_DeviceSilent_IgnoresFilterAndGroupBy()
    {
        AlertDefinition alert = DeviceSilent();
        alert.Filter = All(Cmp("message", ConditionOperator.Contains, "x"));
        alert.GroupByField = "hostname";

        AlertCompileResult result = Compiler.Compile(alert);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.Alert!.AlwaysMatches.Should().BeTrue();
        result.Alert.GroupByField.Should().BeNull();
    }

    [Fact]
    public void Compile_InvalidFilter_SurfacesTheFilterError()
    {
        AlertDefinition alert = Threshold(filter: All(Cmp("bogus_field", ConditionOperator.Equals, "x")));

        Compiler.Compile(alert).Errors.Should().Contain(e => e.StartsWith("Filter:"));
    }

    [Fact]
    public void Compile_InvalidAction_SurfacesTheActionError()
    {
        AlertDefinition alert = Threshold();
        alert.Actions = [new HttpWebhookAction { Url = "not-a-url" }];

        Compiler.Compile(alert).Errors.Should().Contain(e => e.Contains("Action 1"));
    }

    [Fact]
    public void Compile_NoActions_IsAllowed()
    {
        AlertDefinition alert = Threshold();
        alert.Actions = [];

        Compiler.Compile(alert).Success.Should().BeTrue();
    }

    [Fact]
    public void CompileSet_KeepsSurvivors_AndCollectsErrors()
    {
        var good = Threshold(name: "good");
        good.AlertId = 1;
        var bad = Threshold(name: "bad", intervalSeconds: 0);
        bad.AlertId = 2;

        CompiledAlertSet set = Compiler.CompileSet([good, bad]);

        set.AlertCount.Should().Be(1);
        set.Alerts[0].Name.Should().Be("good");
        set.CompileErrors.Should().ContainSingle().Which.AlertId.Should().Be(2);
    }
}
