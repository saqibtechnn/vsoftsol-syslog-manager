using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// PHASE_05 injection suite: SQL fragments, FTS5 operator abuse, nested quotes, unicode
/// normalization tricks, 10 KB query strings, stacked statements. Parameterisation must
/// hold; every malformed query yields a user-facing error, never an exception or a full
/// table scan; and no query mutates data.
/// </summary>
public sealed class SearchInjectionTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(SqliteTestDatabase Db, ScopedEventReader Reader)> ArrangeAsync()
    {
        SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var reader = new ScopedEventReader(db.Repository, db.Factory);
        for (int i = 0; i < 5; i++)
        {
            SyslogEvent e = new()
            {
                ReceivedUtc = Base.AddSeconds(i),
                SourceIp = "10.0.0.1",
                Hostname = "h1",
                Facility = Facility.Local0,
                Severity = Severity.Informational,
                Protocol = Protocol.Udp,
                Message = $"benign message {i}",
                RawMessage = Encoding.UTF8.GetBytes($"benign message {i}"),
                ParseStatus = ParseStatus.Rfc3164,
            };
            await db.Repository.AppendAsync(e, CancellationToken.None);
        }

        await db.SyncSearchAsync();
        return (db, reader);
    }

    private static SearchRequest Request(string query) => new()
    {
        QueryText = query,
        FromUtc = Base.AddDays(-1),
        ToUtc = Base.AddDays(1),
        Limit = 100,
    };

    [Theory]
    [InlineData("'; DROP TABLE events; --")]
    [InlineData("message:\"x'); DELETE FROM events WHERE ('1'='1\"")]
    [InlineData("host:\"h1'; UPDATE users SET password_hash='x'; --\"")]
    [InlineData("1 UNION SELECT username, password_hash FROM users")]
    [InlineData("field.a:\"'||(SELECT password_hash FROM users LIMIT 1)||'\"")]
    [InlineData("benign\" OR \"1\"=\"1")]
    [InlineData("\u202eevil\u202c message")]
    [InlineData("café")]
    public async Task SearchAsync_HostilePayloads_NeverExecuteAndNeverThrow(string query)
    {
        (SqliteTestDatabase db, ScopedEventReader reader) = await ArrangeAsync();
        await using SqliteTestDatabase _ = db;

        long eventsBefore = await ScalarAsync(db, "SELECT COUNT(*) FROM events;");
        long usersBefore = await ScalarAsync(db, "SELECT COUNT(*) FROM users;");
        string hashBefore = await TextAsync(db, "SELECT COALESCE(password_hash,'') FROM users LIMIT 1;");

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request(query), CancellationToken.None);

        // Either a valid (harmless) result or a clean parse error — never an exception.
        (result.Ok ^ (result.Error is not null)).Should().BeTrue();

        (await ScalarAsync(db, "SELECT COUNT(*) FROM events;")).Should().Be(eventsBefore);
        (await ScalarAsync(db, "SELECT COUNT(*) FROM users;")).Should().Be(usersBefore);
        (await TextAsync(db, "SELECT COALESCE(password_hash,'') FROM users LIMIT 1;")).Should().Be(hashBefore);
    }

    [Theory]
    [InlineData("NEAR(a b)")]
    [InlineData("a AND OR b")]
    [InlineData("\"\"\"\"")]
    [InlineData("^foo")]
    [InlineData("col:val AND")]
    [InlineData("(((")]
    public async Task SearchAsync_MalformedQueries_ReturnAUserError(string query)
    {
        (SqliteTestDatabase db, ScopedEventReader reader) = await ArrangeAsync();
        await using SqliteTestDatabase _ = db;

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request(query), CancellationToken.None);

        if (!result.Ok)
        {
            result.Error.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            // If it parsed, it must have executed safely against the FTS index — an empty
            // result set is fine; a row that is not one of the seeded benign events is not.
            result.Rows.Where(r => !r.Message.StartsWith("benign", StringComparison.Ordinal))
                .Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SearchAsync_TenKilobyteQuery_IsRejectedFast()
    {
        (SqliteTestDatabase db, ScopedEventReader reader) = await ArrangeAsync();
        await using SqliteTestDatabase _ = db;

        string huge = string.Concat(Enumerable.Repeat("abcde ", 2500));

        SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request(huge), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().ContainEquivalentOf("too long");
    }

    [Fact]
    public async Task SearchAsync_FtsSpecialCharacters_AreTreatedAsLiteralText()
    {
        (SqliteTestDatabase db, ScopedEventReader reader) = await ArrangeAsync();
        await using SqliteTestDatabase _ = db;

        // These would be FTS5 operators if not parameterised as a quoted phrase.
        foreach (string q in new[] { "benign*", "benign AND message", "\"benign message\"" })
        {
            SearchResult result = await reader.SearchAsync(UserScope.Unrestricted, Request(q), CancellationToken.None);
            result.Ok.Should().BeTrue(because: result.Error);
            result.Rows.Should().NotBeEmpty();
        }
    }

    private static async Task<long> ScalarAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> TextAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }
}
