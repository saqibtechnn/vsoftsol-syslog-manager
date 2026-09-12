using FluentAssertions;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Reports;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Reports;

/// <summary>
/// v1.1 — P10-2 (`docs/evidence/phase-10/known-issues.md`): before this,
/// <c>ReportContentReader</c> discarded <see cref="SqliteAggregationReader.AggregationOutcome"/>
/// entirely once it had the (possibly empty) <c>Result</c> — a broken canned-template
/// aggregation (B10-3/B10-4's exact bug class) and a scope that legitimately excludes every
/// matching stream both looked identical to "no data in this time range." This is the
/// exhaustive mapping every <see cref="SqliteAggregationReader.AggregationStatus"/> value
/// gets, isolated from the real-database plumbing (which
/// <c>ReportContentReaderTests</c>/integration covers for the two statuses reachable through
/// the current public API — <c>Ok</c> and, via a malformed custom event query,
/// <c>SearchResult.Ok == false</c> on the sibling list-report path).
/// </summary>
[Trait("Category", "Reports")]
public sealed class ReportContentReaderErrorMappingTests
{
    [Fact]
    public void Ok_MapsToNoError()
    {
        ReportContentReader.DescribeAggregationFailure(SqliteAggregationReader.AggregationStatus.Ok, detail: null)
            .Should().BeNull();
    }

    [Fact]
    public void BadQuery_MapsToTheParserDetail()
    {
        ReportContentReader.DescribeAggregationFailure(SqliteAggregationReader.AggregationStatus.BadQuery, "unexpected token at 4")
            .Should().Be("unexpected token at 4");
    }

    [Fact]
    public void BadAggregation_MapsToTheCompilerDetail()
    {
        ReportContentReader.DescribeAggregationFailure(SqliteAggregationReader.AggregationStatus.BadAggregation, "'host' cannot be grouped")
            .Should().Be("'host' cannot be grouped");
    }

    [Fact]
    public void ScopeExcludesEverything_MapsToAPlainEnglishReason_NotTheRawDetail()
    {
        // "scope excludes everything" (AggregationCompiler's internal rejection string) is
        // not something an Operator reading a report should ever see verbatim.
        string? result = ReportContentReader.DescribeAggregationFailure(
            SqliteAggregationReader.AggregationStatus.ScopeExcludesEverything, "scope excludes everything");

        result.Should().NotBeNullOrEmpty();
        result.Should().NotBe("scope excludes everything");
    }

    [Fact]
    public void UnknownStatusWithNoDetail_FallsBackToTheStatusName_NeverToNull()
    {
        // Defensive: a future AggregationStatus value with a null Detail must still surface
        // *something* rather than silently reverting to the "no data" look this fix exists
        // to close.
        ReportContentReader.DescribeAggregationFailure(SqliteAggregationReader.AggregationStatus.BadAggregation, detail: null)
            .Should().Be(nameof(SqliteAggregationReader.AggregationStatus.BadAggregation));
    }
}
