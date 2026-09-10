using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 aggregation oracle (TESTING_STANDARDS §1 — differential): every aggregation
/// function, grouped and flat, bucketed and flat, verified against an independently written
/// SQL query over the same fixture. "Sums, counts, and distinct counts are exactly the kind
/// of thing that looks right and is wrong."
/// </summary>
public sealed class AggregationOracleTests : IAsyncLifetime
{
    private DashboardTestHarness _h = null!;
    private readonly DateTimeOffset _from = AggregationFixture.WindowStart;
    private readonly DateTimeOffset _to = AggregationFixture.WindowEnd;

    public async Task InitializeAsync()
    {
        _h = await DashboardTestHarness.CreateAsync(seed: false);
        await _h.SeedEventsAsync(AggregationFixture.Build());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<SqliteAggregationReader.AggregationOutcome> Run(AggregationSpec spec, string query = "", BucketInterval bucket = BucketInterval.None) =>
        _h.Aggregation.AggregateAsync(UserScope.Unrestricted, query, spec, _from, _to, bucket, CancellationToken.None);

    [Fact]
    public async Task Count_Ungrouped_MatchesSelectCount()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(new AggregationSpec { Function = AggregationFunction.Count });

        double expected = await HandScalar("SELECT COUNT(*) FROM events e WHERE InWindow(e)");
        outcome.Result.Scalar().Should().Be(expected);
        expected.Should().Be(AggregationFixture.EventCount);
    }

    [Fact]
    public async Task Count_GroupedByHostname_MatchesGroupBy()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 50 });

        Dictionary<string, double> expected = await HandGroups(
            "SELECT COALESCE(hostname,'(none)'), COUNT(*) FROM events e WHERE InWindow(e) GROUP BY hostname");

        Actual(outcome).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Count_BucketedHourly_EveryBucketMatches_AndSumsToTheTotal()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Count }, bucket: BucketInterval.OneHour);

        outcome.Result.Plan!.Count.Should().Be(6);
        outcome.Result.Points.Sum(p => p.Value).Should().Be(AggregationFixture.EventCount);

        for (int b = 0; b < 6; b++)
        {
            DateTimeOffset bucketStart = _from.AddHours(b);
            DateTimeOffset bucketEnd = bucketStart.AddHours(1);
            double expected = await HandScalar(
                "SELECT COUNT(*) FROM events e WHERE e.received_utc >= $bs AND e.received_utc < $be",
                ("$bs", Ts(bucketStart)), ("$be", Ts(bucketEnd)));
            double got = outcome.Result.Points.Where(p => p.BucketIndex == b).Sum(p => p.Value);
            got.Should().Be(expected, $"bucket {b}");
        }
    }

    [Fact]
    public async Task DistinctCount_OfSourceIp_MatchesCountDistinct()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.DistinctCount, ValueField = "source_ip" });

        double expected = await HandScalar("SELECT COUNT(DISTINCT source_ip) FROM events e WHERE InWindow(e)");
        outcome.Result.Scalar().Should().Be(expected);
    }

    [Fact]
    public async Task Sum_OfOccurrenceCount_GroupedByHost_MatchesGroupBySum()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Sum, ValueField = "occurrence_count", GroupByField = "hostname", TopN = 50 });

        Dictionary<string, double> expected = await HandGroups(
            "SELECT COALESCE(hostname,'(none)'), SUM(occurrence_count) FROM events e WHERE InWindow(e) GROUP BY hostname");

        Actual(outcome).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Average_OfSeverity_MatchesAvg()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Average, ValueField = "severity" });

        double expected = await HandScalar("SELECT AVG(severity) FROM events e WHERE InWindow(e)");
        outcome.Result.Scalar().Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public async Task MinAndMax_OfFacility_MatchSqlMinMax()
    {
        double min = (await Run(new AggregationSpec { Function = AggregationFunction.Min, ValueField = "facility" })).Result.Scalar();
        double max = (await Run(new AggregationSpec { Function = AggregationFunction.Max, ValueField = "facility" })).Result.Scalar();

        min.Should().Be(await HandScalar("SELECT MIN(facility) FROM events e WHERE InWindow(e)"));
        max.Should().Be(await HandScalar("SELECT MAX(facility) FROM events e WHERE InWindow(e)"));
    }

    [Fact]
    public async Task Sum_OfAnExtractedNumericField_MatchesTheJoinedSql()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Sum, ValueField = "field.bytes" });

        double expected = await HandScalar("""
            SELECT COALESCE(SUM(CAST(ef.value AS REAL)), 0)
            FROM events e JOIN event_fields ef ON ef.event_id = e.event_id AND ef.name = 'bytes'
            WHERE InWindow(e)
            """);

        outcome.Result.Scalar().Should().BeApproximately(expected, 1e-6);
        expected.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Count_WithAQueryFilter_AppliesThePredicate()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Count }, query: "app:sshd");

        double expected = await HandScalar("SELECT COUNT(*) FROM events e WHERE InWindow(e) AND e.app_name = 'sshd'");
        outcome.Result.Scalar().Should().Be(expected);
        expected.Should().BeLessThan(AggregationFixture.EventCount);
    }

    [Fact]
    public async Task Count_GroupedByExtractedField_MatchesTheJoinedGroupBy()
    {
        SqliteAggregationReader.AggregationOutcome outcome = await Run(
            new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "field.bytes", TopN = 50 });

        Dictionary<string, double> expected = await HandGroups("""
            SELECT COALESCE((SELECT ef.value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.name = 'bytes' LIMIT 1), '(none)'),
                   COUNT(*)
            FROM events e WHERE InWindow(e)
            GROUP BY 1
            """);

        Actual(outcome).Should().BeEquivalentTo(expected);
    }

    // ---- oracle plumbing -------------------------------------------------

    private static Dictionary<string, double> Actual(SqliteAggregationReader.AggregationOutcome outcome)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (AggPoint p in outcome.Result.Points)
        {
            string key = p.GroupKey ?? "(none)";
            totals[key] = totals.GetValueOrDefault(key) + p.Value;
        }

        return totals;
    }

    private static string Ts(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private async Task<double> HandScalar(string sql, params (string Name, object Value)[] extra)
    {
        await using SqliteConnection c = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = Expand(sql);
        cmd.Parameters.AddWithValue("$from", Ts(_from));
        cmd.Parameters.AddWithValue("$to", Ts(_to));
        foreach ((string n, object v) in extra)
        {
            cmd.Parameters.AddWithValue(n, v);
        }

        object? raw = await cmd.ExecuteScalarAsync(CancellationToken.None);
        return raw is null or DBNull ? 0 : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<string, double>> HandGroups(string sql)
    {
        await using SqliteConnection c = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = Expand(sql);
        cmd.Parameters.AddWithValue("$from", Ts(_from));
        cmd.Parameters.AddWithValue("$to", Ts(_to));

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result[reader.GetString(0)] = reader.GetDouble(1);
        }

        return result;
    }

    private static string Expand(string sql) =>
        sql.Replace("InWindow(e)", "e.received_utc >= $from AND e.received_utc < $to", StringComparison.Ordinal);
}
