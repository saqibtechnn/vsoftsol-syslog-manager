using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 security — the highest risk. Two users with different scopes load the same
/// shared dashboard widget; each must see only their own data, including in aggregate
/// counts. "An aggregate that includes out-of-scope events is a data leak even when no
/// individual event is displayed."
/// </summary>
public sealed class AggregationScopeTests : IAsyncLifetime
{
    private DashboardTestHarness _h = null!;
    private readonly DateTimeOffset _from = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly DateTimeOffset _to = new(2026, 6, 1, 6, 0, 0, TimeSpan.Zero);

    private long _streamA;
    private long _streamB;
    private const int InA = 120;
    private const int InB = 45;

    public async Task InitializeAsync()
    {
        _h = await DashboardTestHarness.CreateAsync(seed: false);

        _streamA = await Exec("INSERT INTO streams (name, enabled, sort_order, created_utc) VALUES ('A', 1, 10, 't'); SELECT last_insert_rowid();");
        _streamB = await Exec("INSERT INTO streams (name, enabled, sort_order, created_utc) VALUES ('B', 1, 11, 't'); SELECT last_insert_rowid();");

        var events = new List<SyslogEvent>();
        for (int i = 0; i < InA + InB; i++)
        {
            events.Add(new SyslogEvent
            {
                ReceivedUtc = _from.AddMinutes(i % 300),
                SourceIp = $"10.9.{i % 3}.{i % 100 + 1}",
                Hostname = i < InA ? "in-scope" : "out-of-scope",
                AppName = "sshd",
                Facility = Facility.Local0,
                Severity = (Severity)(i % 8),
                Protocol = Protocol.Udp,
                Message = $"m{i}",
                RawMessage = System.Text.Encoding.UTF8.GetBytes($"m{i}"),
                ParseStatus = ParseStatus.Rfc5424,
            });
        }

        IReadOnlyList<long> ids = await _h.Db.Repository.AppendBatchAsync(events, CancellationToken.None);
        for (int i = 0; i < ids.Count; i++)
        {
            await Exec($"INSERT INTO event_streams (event_id, stream_id) VALUES ({ids[i]}, {(i < InA ? _streamA : _streamB)});");
        }
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private UserScope ScopeFor(long streamId) => UserScope.Create([streamId], null);

    [Fact]
    public async Task Count_UnderTwoScopes_ReturnsEachViewersOwnTotal()
    {
        var spec = new AggregationSpec { Function = AggregationFunction.Count };

        double a = (await _h.Aggregation.AggregateAsync(ScopeFor(_streamA), "", spec, _from, _to, BucketInterval.None, CancellationToken.None)).Result.Scalar();
        double b = (await _h.Aggregation.AggregateAsync(ScopeFor(_streamB), "", spec, _from, _to, BucketInterval.None, CancellationToken.None)).Result.Scalar();
        double all = (await _h.Aggregation.AggregateAsync(UserScope.Unrestricted, "", spec, _from, _to, BucketInterval.None, CancellationToken.None)).Result.Scalar();

        a.Should().Be(InA);
        b.Should().Be(InB);
        all.Should().Be(InA + InB);
    }

    [Fact]
    public async Task GroupedAggregate_UnderAScope_NeverNamesAnOutOfScopeGroup()
    {
        var spec = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 50 };

        SqliteAggregationReader.AggregationOutcome a =
            await _h.Aggregation.AggregateAsync(ScopeFor(_streamA), "", spec, _from, _to, BucketInterval.None, CancellationToken.None);

        a.Result.GroupKeys().Should().Contain("in-scope");
        a.Result.GroupKeys().Should().NotContain("out-of-scope");
        a.Result.Points.Sum(p => p.Value).Should().Be(InA);
    }

    [Fact]
    public async Task DistinctCount_UnderAScope_CountsOnlyInScopeValues()
    {
        var spec = new AggregationSpec { Function = AggregationFunction.DistinctCount, ValueField = "hostname" };

        double a = (await _h.Aggregation.AggregateAsync(ScopeFor(_streamA), "", spec, _from, _to, BucketInterval.None, CancellationToken.None)).Result.Scalar();

        a.Should().Be(1, "only 'in-scope' is visible under stream A");
    }

    [Fact]
    public async Task AScopeThatSeesNoData_ReturnsAnEmptyResult_NotAnError()
    {
        var spec = new AggregationSpec { Function = AggregationFunction.Count };
        UserScope seesNothing = UserScope.Create([987654], null); // a stream id that matches no events

        SqliteAggregationReader.AggregationOutcome outcome =
            await _h.Aggregation.AggregateAsync(seesNothing, "", spec, _from, _to, BucketInterval.None, CancellationToken.None);

        outcome.Status.Should().Be(SqliteAggregationReader.AggregationStatus.Ok, "a scoped user gets their (empty) data, not an error");
        outcome.Result.Scalar().Should().Be(0);
        outcome.Result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task BucketedSeries_UnderAScope_SumsToTheInScopeTotalOnly()
    {
        var spec = new AggregationSpec { Function = AggregationFunction.Count };

        SqliteAggregationReader.AggregationOutcome a =
            await _h.Aggregation.AggregateAsync(ScopeFor(_streamA), "", spec, _from, _to, BucketInterval.OneHour, CancellationToken.None);

        a.Result.Points.Sum(p => p.Value).Should().Be(InA);
    }

    private async Task<long> Exec(string sql)
    {
        await using SqliteConnection c = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
