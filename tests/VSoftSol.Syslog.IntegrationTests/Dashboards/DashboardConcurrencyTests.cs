using System.Diagnostics;
using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 Validation — concurrency: 20 simultaneous dashboard loads; the cache holds and
/// the load stays fast. (The &lt; 3 s target against the 50M-event database is the
/// <c>DashboardBenchmark</c> acceptance gate, carried to the Phase 12 clean-VM run as P9-1;
/// here the assertion is that concurrency is safe and the cache collapses the work.)
/// </summary>
public sealed class DashboardConcurrencyTests : IAsyncLifetime
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

    [Fact]
    public async Task Twenty_ConcurrentLoadsOfOneWidget_ComputeOnce_AndAllAgree()
    {
        int computed = 0;
        var spec = new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 50 };
        string key = AggregationCache.WidgetKey(WidgetSource.MatchAll, spec, _from, _to, BucketInterval.None);

        var sw = Stopwatch.StartNew();
        SqliteAggregationReader.AggregationOutcome[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _h.Cache.GetOrAddAsync(
                UserScope.Unrestricted, key,
                async ct =>
                {
                    Interlocked.Increment(ref computed);
                    return await _h.Aggregation.AggregateAsync(UserScope.Unrestricted, "", spec, _from, _to, BucketInterval.None, ct);
                },
                CancellationToken.None)));
        sw.Stop();

        computed.Should().Be(1, "the cache collapses 20 concurrent callers to one query");
        double first = results[0].Result.Points.Sum(p => p.Value);
        results.Should().OnlyContain(r => r.Result.Points.Sum(p => p.Value) == first);
        first.Should().Be(AggregationFixture.EventCount);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Twenty_ConcurrentLoadsOfAFourWidgetDashboard_AllSucceed()
    {
        AggregationSpec[] widgets =
        [
            new() { Function = AggregationFunction.Count, Bucket = BucketInterval.OneHour },
            new() { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 10 },
            new() { Function = AggregationFunction.Count, GroupByField = "severity" },
            new() { Function = AggregationFunction.DistinctCount, ValueField = "source_ip" },
        ];

        var sw = Stopwatch.StartNew();
        int[] counts = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            int total = 0;
            foreach (AggregationSpec spec in widgets)
            {
                string key = AggregationCache.WidgetKey(WidgetSource.MatchAll, spec, _from, _to, spec.Bucket);
                SqliteAggregationReader.AggregationOutcome outcome = await _h.Cache.GetOrAddAsync(
                    UserScope.Unrestricted, key,
                    ct => _h.Aggregation.AggregateAsync(UserScope.Unrestricted, "", spec, _from, _to, spec.Bucket, ct),
                    CancellationToken.None);
                total += outcome.Result.Points.Count;
            }

            return total;
        }));
        sw.Stop();

        counts.Should().OnlyContain(c => c == counts[0]);
        counts[0].Should().BeGreaterThan(0);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }
}
