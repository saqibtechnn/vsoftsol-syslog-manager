using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>End-to-end search against a real database: the query language, time range, sort, and paging.</summary>
public sealed class SearchExecutionTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static SearchRequest Request(string query, int limit = 100) => new()
    {
        QueryText = query,
        FromUtc = Base.AddDays(-1),
        ToUtc = Base.AddDays(1),
        Limit = limit,
    };

    private static SyslogEvent Evt(string message, DateTimeOffset received, Severity severity = Severity.Informational,
        string host = "host-a", string ip = "10.0.0.1", IReadOnlyList<EventField>? fields = null) => new()
        {
            ReceivedUtc = received,
            SourceIp = ip,
            Hostname = host,
            AppName = "sshd",
            Facility = Facility.SecurityAuth,
            Severity = severity,
            Protocol = Protocol.Udp,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = ParseStatus.Rfc3164,
            Fields = fields ?? [],
        };

    [Fact]
    public async Task SearchAsync_FreeText_ReturnsMatchingRowsOnly()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        await db.Repository.AppendAsync(Evt("failed password for root", Base), CancellationToken.None);
        await db.Repository.AppendAsync(Evt("accepted publickey for admin", Base.AddMinutes(1)), CancellationToken.None);
        await db.SyncSearchAsync();

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request("failed"), CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Rows.Should().ContainSingle().Which.Message.Should().Be("failed password for root");
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_InvalidQuery_ReturnsErrorNotException()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request("severity:banana"), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().ContainEquivalentOf("severity");
        result.ErrorPosition.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SearchAsync_TimeRange_IsAlwaysApplied()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        await db.Repository.AppendAsync(Evt("inside window", Base), CancellationToken.None);
        await db.Repository.AppendAsync(Evt("way in the past", Base.AddYears(-1)), CancellationToken.None);
        await db.SyncSearchAsync();

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request(string.Empty), CancellationToken.None);

        result.Rows.Should().ContainSingle().Which.Message.Should().Be("inside window");
    }

    [Fact]
    public async Task SearchAsync_FieldAndBoolean_Compose()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        await db.Repository.AppendAsync(Evt("link down", Base, Severity.Error, host: "sw-1"), CancellationToken.None);
        await db.Repository.AppendAsync(Evt("link down", Base.AddMinutes(1), Severity.Warning, host: "sw-2"), CancellationToken.None);
        await db.SyncSearchAsync();

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted,
            Request("\"link down\" AND severity:error"), CancellationToken.None);

        result.Rows.Should().ContainSingle().Which.Hostname.Should().Be("sw-1");
    }

    [Fact]
    public async Task SearchAsync_Paging_DoesNotDuplicateOrSkipRows()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        for (int i = 0; i < 50; i++)
        {
            await db.Repository.AppendAsync(Evt($"row {i}", Base.AddSeconds(i)), CancellationToken.None);
        }

        await db.SyncSearchAsync();

        var seen = new List<long>();
        for (long offset = 0; offset < 50; offset += 10)
        {
            SearchRequest page = Request(string.Empty, limit: 10) with { Offset = offset };
            SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, page, CancellationToken.None);
            seen.AddRange(result.Rows.Select(r => r.EventId));
        }

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().HaveCount(50);
    }

    [Fact]
    public async Task SearchAsync_Sort_BySeverityAscending_OrdersMostSevereFirst()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        await db.Repository.AppendAsync(Evt("a", Base, Severity.Warning), CancellationToken.None);
        await db.Repository.AppendAsync(Evt("b", Base.AddMinutes(1), Severity.Emergency), CancellationToken.None);
        await db.Repository.AppendAsync(Evt("c", Base.AddMinutes(2), Severity.Error), CancellationToken.None);
        await db.SyncSearchAsync();

        SearchRequest request = Request(string.Empty) with { Sort = new SearchSort(SearchSortField.Severity, Descending: false) };
        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, request, CancellationToken.None);

        result.Rows.Select(r => r.Severity).Should().ContainInOrder(Severity.Emergency, Severity.Error, Severity.Warning);
    }

    [Fact]
    public async Task SearchAsync_HydratesExtractedFields()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        SyslogEvent evt = Evt("with fields", Base, fields: [new EventField("srcport", "22"), new EventField("user", "root")]);
        await db.Repository.AppendAsync(evt, CancellationToken.None);
        await db.SyncSearchAsync();

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request("field.user:root"), CancellationToken.None);

        result.Rows.Should().ContainSingle()
            .Which.Fields.Should().Contain(f => f.Name == "srcport" && f.Value == "22");
    }
}
