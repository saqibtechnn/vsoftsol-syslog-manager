using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Rules.Conditions;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Conditions.ConditionTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Conditions;

public sealed class ConditionCompilerTests
{
    private static ConditionCompileResult Compile(ConditionNode node) => new ConditionCompiler().Compile(node);

    [Fact]
    public void UnknownField_IsAnError()
    {
        ConditionCompileResult r = Compile(Cmp("bogus_field", ConditionOperator.Equals, "x"));
        r.Success.Should().BeFalse();
        string.Join(" ", r.Errors).Should().ContainEquivalentOf("Unknown field");
    }

    [Theory]
    [InlineData(ConditionOperator.Equals)]
    [InlineData(ConditionOperator.Contains)]
    [InlineData(ConditionOperator.Matches)]
    public void MissingValue_ForValueOperators_IsAnError(ConditionOperator op)
    {
        ConditionCompileResult r = Compile(Cmp("message", op, string.Empty));
        r.Success.Should().BeFalse();
        string.Join(" ", r.Errors).Should().ContainEquivalentOf("value");
    }

    [Fact]
    public void GreaterThan_WithNonNumericValue_IsAnError()
    {
        ConditionCompileResult r = Compile(Cmp("occurrence_count", ConditionOperator.GreaterThan, "lots"));
        r.Success.Should().BeFalse();
        string.Join(" ", r.Errors).Should().ContainEquivalentOf("number");
    }

    [Fact]
    public void Exists_NeedsNoValue()
    {
        Compile(Cmp("field.anything", ConditionOperator.Exists)).Success.Should().BeTrue();
        Compile(Cmp("message", ConditionOperator.NotExists)).Success.Should().BeTrue();
    }

    [Fact]
    public void DeeplyNestedTree_IsRejected()
    {
        ConditionNode node = Cmp("message", ConditionOperator.Contains, "x");
        for (int i = 0; i < 20; i++)
        {
            node = All(node);
        }

        ConditionCompileResult r = Compile(node);
        r.Success.Should().BeFalse();
        string.Join(" ", r.Errors).Should().ContainEquivalentOf("deep");
    }

    [Fact]
    public void HugeTree_IsRejected()
    {
        var big = new ConditionGroup { Join = ConditionJoin.Or };
        for (int i = 0; i < 500; i++)
        {
            big.Children.Add(Cmp("message", ConditionOperator.Contains, "x" + i));
        }

        ConditionCompileResult r = Compile(big);
        r.Success.Should().BeFalse();
        string.Join(" ", r.Errors).Should().ContainEquivalentOf("many parts");
    }

    [Fact]
    public void InList_ParsesCommaAndNewlineSeparatedValues()
    {
        ConditionCompileResult r = Compile(Cmp("hostname", ConditionOperator.InList, "a, b\nc,  d "));
        r.Success.Should().BeTrue();
    }
}
