using FluentAssertions;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Search;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Search;

/// <summary>
/// The SQL compiler must parameterise every user-supplied value and must never place user
/// bytes into the SQL text (SECURITY_STANDARDS.md §5.1). These assert that property against
/// injection payloads, plus the structural shape of each predicate.
/// </summary>
public sealed class SearchCompilerTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    private static CompiledSearch Compile(string queryText, UserScope? scope = null, SearchRequest? request = null)
    {
        SearchParseResult parsed = SearchQueryParser.Parse(queryText);
        parsed.Success.Should().BeTrue(because: parsed.Error);
        request ??= new SearchRequest { FromUtc = From, ToUtc = To, QueryText = queryText };
        return new SearchCompiler().Compile(request, parsed.Query!, scope ?? UserScope.Unrestricted);
    }

    [Theory]
    [InlineData("'; DROP TABLE events; --")]
    [InlineData("host:\"x'; DELETE FROM events; --\"")]
    [InlineData("message:\") OR 1=1 --\"")]
    [InlineData("field.a:\"1 UNION SELECT password_hash FROM users\"")]
    [InlineData("vendor:\"; ATTACH DATABASE 'x' AS y; --\"")]
    public void Compile_InjectionPayloads_NeverAppearInSqlText(string queryText)
    {
        CompiledSearch compiled = Compile(queryText);

        // The dangerous substrings must live only in parameter values, never in the SQL.
        compiled.WhereSql.Should().NotContainAny("DROP", "DELETE", "UNION", "ATTACH", "1=1", "--", "';");
        compiled.WhereSql.Should().NotContain("'"); // no string literals at all
        compiled.Parameters.Should().NotBeEmpty();
    }

    [Fact]
    public void Compile_EveryValue_IsABoundParameter_NotInlined()
    {
        CompiledSearch compiled = Compile("host:web01 vendor:cisco-ios field.portname:eth0 \"login failed\"");

        foreach (string literal in new[] { "web01", "cisco-ios", "eth0", "login failed", "portname" })
        {
            compiled.WhereSql.Should().NotContainEquivalentOf(literal,
                because: $"'{literal}' must be a parameter value, not SQL text");
        }

        compiled.Parameters.Select(p => p.Value.ToString())
            .Should().Contain(new[] { "web01", "cisco-ios", "eth0", "portname", "\"login failed\"" });
    }

    [Fact]
    public void Compile_AlwaysIncludesTheTimeRange()
    {
        CompiledSearch compiled = Compile("");

        compiled.WhereSql.Should().Contain("e.received_utc >=").And.Contain("e.received_utc <");
        compiled.Parameters.Should().HaveCount(2);
        compiled.MatchesNothing.Should().BeFalse();
    }

    [Fact]
    public void Compile_ScopeRestrictedUser_AddsStreamMembershipClause()
    {
        var scope = UserScope.Create(streamIds: [7, 8], deviceGroupIds: null);

        CompiledSearch compiled = Compile("failed", scope);

        compiled.WhereSql.Should().Contain("event_streams").And.Contain("stream_id IN");
        compiled.Parameters.Select(p => p.Value).Should().Contain(new object[] { 7L, 8L });
    }

    [Fact]
    public void Compile_UnrestrictedScope_AddsNoScopeClause()
    {
        CompiledSearch compiled = Compile("failed", UserScope.Unrestricted);

        compiled.WhereSql.Should().NotContain("device_group_members");
        compiled.WhereSql.Should().NotContain("event_streams es WHERE es.event_id = e.event_id AND es.stream_id");
    }

    [Theory]
    [InlineData(SearchSortField.ReceivedUtc, true, "e.received_utc DESC, e.event_id DESC")]
    [InlineData(SearchSortField.Severity, false, "e.severity ASC, e.event_id ASC")]
    [InlineData(SearchSortField.Host, true, "e.hostname DESC, e.event_id DESC")]
    public void Compile_Sort_MapsEnumToAllowListedColumn(SearchSortField field, bool desc, string expected)
    {
        var request = new SearchRequest
        {
            FromUtc = From,
            ToUtc = To,
            QueryText = string.Empty,
            Sort = new SearchSort(field, desc),
        };

        CompiledSearch compiled = Compile(string.Empty, request: request);

        compiled.OrderBySql.Should().Be(expected);
    }

    [Fact]
    public void Compile_NotEqualsOnNullableColumn_GuardsAgainstNull()
    {
        CompiledSearch compiled = Compile("host:!=web01");

        // SQLite: hostname <> 'web01' is NULL for a NULL hostname; the guard keeps parity
        // with the oracle (a NULL column never satisfies !=).
        compiled.WhereSql.Should().Contain("e.hostname IS NOT NULL AND e.hostname <>");
    }

    [Fact]
    public void Compile_StreamNotEquals_UsesNotExists()
    {
        CompiledSearch compiled = Compile("stream:!=Windows");

        compiled.WhereSql.Should().Contain("NOT EXISTS");
    }
}
