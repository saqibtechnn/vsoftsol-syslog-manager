using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Search;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Alerts;

/// <summary>
/// PHASE_08 item 7 — "promote to alert from a saved search". A search query is translated
/// best-effort to a <see cref="ConditionGroup"/>; anything lossy is reported, never dropped
/// silently.
/// </summary>
public sealed class SearchToConditionTranslatorTests
{
    private static SearchConditionTranslation Translate(string query)
    {
        SearchParseResult parsed = SearchQueryParser.Parse(query);
        parsed.Success.Should().BeTrue(parsed.Error);
        return SearchToConditionTranslator.Translate(parsed.Query);
    }

    [Fact]
    public void EmptyQuery_ProducesNoFilter()
    {
        SearchConditionTranslation t = Translate("");

        t.Filter.Should().BeNull();
        t.Exact.Should().BeTrue();
    }

    [Fact]
    public void BareText_BecomesMessageContains()
    {
        SearchConditionTranslation t = Translate("failed");

        t.Exact.Should().BeTrue();
        var cmp = t.Filter!.Children.Should().ContainSingle().Which.Should().BeOfType<ConditionComparison>().Subject;
        cmp.Field.Should().Be("message");
        cmp.Operator.Should().Be(ConditionOperator.Contains);
        cmp.Value.Should().Be("failed");
    }

    [Fact]
    public void FieldTerm_MapsToConditionComparison()
    {
        SearchConditionTranslation t = Translate("host:core-sw-1");

        ConditionComparison cmp = (ConditionComparison)t.Filter!.Children[0];
        cmp.Field.Should().Be("hostname");
        cmp.Operator.Should().Be(ConditionOperator.Equals);
        cmp.Value.Should().Be("core-sw-1");
    }

    [Fact]
    public void SeverityRange_MapsToGreaterThan()
    {
        SearchConditionTranslation t = Translate("severity:>3");

        ConditionComparison cmp = (ConditionComparison)t.Filter!.Children[0];
        cmp.Field.Should().Be("severity");
        cmp.Operator.Should().Be(ConditionOperator.GreaterThan);
    }

    [Fact]
    public void GreaterThanOrEqual_IsFlaggedAsInexact()
    {
        SearchConditionTranslation t = Translate("severity:>=3");

        t.Exact.Should().BeFalse();
        t.Unsupported.Should().Contain(u => u.Contains(">="));
    }

    [Fact]
    public void AndOfTerms_BecomesAndGroup()
    {
        SearchConditionTranslation t = Translate("host:core-sw-1 AND failed");

        t.Filter!.Join.Should().Be(ConditionJoin.And);
        t.Filter.Children.Should().HaveCount(2);
    }

    [Fact]
    public void OrOfTerms_BecomesOrGroup()
    {
        SearchConditionTranslation t = Translate("failed OR denied");

        t.Filter!.Join.Should().Be(ConditionJoin.Or);
    }

    [Fact]
    public void NegatedFieldTerm_BecomesNotEquals()
    {
        SearchConditionTranslation t = Translate("-host:noisy");

        ConditionComparison cmp = (ConditionComparison)t.Filter!.Children[0];
        cmp.Operator.Should().Be(ConditionOperator.NotEquals);
    }

    [Fact]
    public void ReferenceField_IsDroppedAndFlagged()
    {
        SearchConditionTranslation t = Translate("device:core-sw-1");

        t.Filter.Should().BeNull();
        t.Unsupported.Should().Contain(u => u.Contains("device:"));
    }

    [Fact]
    public void CustomField_MapsToExtractedField()
    {
        SearchConditionTranslation t = Translate("field.srcport:22");

        ConditionComparison cmp = (ConditionComparison)t.Filter!.Children[0];
        cmp.Field.Should().Be("field.srcport");
    }

    [Fact]
    public void Wildcard_BecomesContains_AndIsFlagged()
    {
        SearchConditionTranslation t = Translate("host:core*");

        t.Exact.Should().BeFalse();
        ConditionComparison cmp = (ConditionComparison)t.Filter!.Children[0];
        cmp.Operator.Should().Be(ConditionOperator.StartsWith);
    }
}
