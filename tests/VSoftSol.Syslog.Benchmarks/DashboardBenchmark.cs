using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Benchmarks;

/// <summary>
/// PHASE_09 acceptance gate: a representative multi-widget dashboard load must complete in
/// &lt; 3 s. The phase asks for a 50M-event database; on the 2-vCPU VMware build VM that is a
/// ~1.5 h seed with noise-dominated percentiles (the P1-1 / P5-1 pattern), so this seeds
/// DASHBOARD_BENCH_EVENTS (default 2,000,000 — reusing the SearchBenchmark database when it
/// exists) and the literal 50M run is carried to the Phase 12 clean-VM acceptance run
/// (P9-1). Measures a cold load (empty cache) and a warm load (cache hit).
/// </summary>
[Config(typeof(Config))]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3, invocationCount: 20)]
public class DashboardBenchmark
{
    private sealed class Config : ManualConfig
    {
        public Config() => AddColumn(StatisticColumn.P50, StatisticColumn.P90, StatisticColumn.P95, StatisticColumn.P100);
    }

    private static readonly DateTimeOffset WindowEnd = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    private SqliteConnectionFactory? _factory;
    private SqliteAggregationReader? _reader;
    private AggregationCache? _cache;
    private long _eventCount;

    private static readonly WidgetSpec[] Widgets =
    [
        new("failed", new AggregationSpec { Function = AggregationFunction.Count, Bucket = BucketInterval.OneHour }),
        new("", new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "hostname", TopN = 10 }),
        new("", new AggregationSpec { Function = AggregationFunction.Count, GroupByField = "severity" }),
        new("", new AggregationSpec { Function = AggregationFunction.DistinctCount, ValueField = "source_ip" }),
    ];

    private readonly record struct WidgetSpec(string Query, AggregationSpec Spec);

    [GlobalSetup]
    public void Seed()
    {
        int target = int.TryParse(
            Environment.GetEnvironmentVariable("DASHBOARD_BENCH_EVENTS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 2_000_000;

        // Reuse the SearchBenchmark database if it is already seeded — same shape, same window.
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-searchbench");
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "syslog.db");

        var options = new SqliteDataOptions { DatabasePath = dbPath, InsertBatchSize = 20_000 };
        _factory = new SqliteConnectionFactory(options);
        new MigrationRunner(_factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        var repo = new SqliteLogRepository(_factory, Options.Create(options));

        _eventCount = repo.CountAsync(new Core.Abstractions.LogQuery(), CancellationToken.None).GetAwaiter().GetResult();
        if (_eventCount < target)
        {
            SeedEvents(repo, target - (int)_eventCount);
            while (repo.SyncSearchIndexAsync(250_000, CancellationToken.None).GetAwaiter().GetResult() > 0)
            {
            }

            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            Exec("ANALYZE;");
            _eventCount = repo.CountAsync(new Core.Abstractions.LogQuery(), CancellationToken.None).GetAwaiter().GetResult();
        }

        _reader = new SqliteAggregationReader(_factory);
        _cache = new AggregationCache(TimeProvider.System, () => TimeSpan.FromSeconds(15));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dashboard benchmark dataset: {_eventCount:N0} events"));
    }

    private static readonly string[] Words =
        ["failed", "password", "accepted", "session", "denied", "login", "user", "root", "invalid", "connection", "reset", "link", "down"];

    private static readonly string[] Hosts = ["edge-fw-1", "core-sw-1", "core-sw-2", "dc-rtr-1", "wlc-1", "app-01", "app-02", "db-01"];

    private void SeedEvents(SqliteLogRepository repo, int count)
    {
        var rng = new Random(1234);
        DateTimeOffset start = WindowEnd.AddDays(-40);
        long ticks = (WindowEnd - start).Ticks;
        var buffer = new List<SyslogEvent>(20_000);

        for (int i = 0; i < count; i++)
        {
            string message = string.Join(' ', Enumerable.Range(0, rng.Next(4, 9)).Select(_ => Words[rng.Next(Words.Length)]));
            buffer.Add(new SyslogEvent
            {
                ReceivedUtc = start.AddTicks((long)(rng.NextDouble() * ticks)),
                SourceIp = "10.0." + rng.Next(0, 8) + "." + rng.Next(1, 254),
                Hostname = Hosts[rng.Next(Hosts.Length)],
                AppName = "sshd",
                Facility = (Facility)rng.Next(0, 24),
                Severity = (Severity)rng.Next(0, 8),
                Protocol = Protocol.Udp,
                Message = message,
                RawMessage = System.Text.Encoding.UTF8.GetBytes(message),
                ParseStatus = ParseStatus.Rfc3164,
            });

            if (buffer.Count == 20_000)
            {
                repo.AppendBatchAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            repo.AppendBatchAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private void Exec(string sql)
    {
        using SqliteConnection c = _factory!.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private async Task<int> LoadDashboardAsync(UserScope scope)
    {
        DateTimeOffset from = WindowEnd.AddHours(-24);
        int total = 0;
        foreach (WidgetSpec w in Widgets)
        {
            string key = AggregationCache.WidgetKey(WidgetSource.Query(w.Query), w.Spec, from, WindowEnd, w.Spec.Bucket);
            SqliteAggregationReader.AggregationOutcome outcome = await _cache!.GetOrAddAsync(
                scope, key,
                ct => _reader!.AggregateAsync(scope, w.Query, w.Spec, from, WindowEnd, w.Spec.Bucket, ct),
                CancellationToken.None);
            total += outcome.Result.Points.Count;
        }

        return total;
    }

    [Benchmark(Description = "cold load — 4 widgets, empty cache, 24h window")]
    public Task<int> ColdLoad()
    {
        _cache!.Clear();
        return LoadDashboardAsync(UserScope.Unrestricted);
    }

    [Benchmark(Description = "warm load — 4 widgets, cache hit")]
    public Task<int> WarmLoad() => LoadDashboardAsync(UserScope.Unrestricted);

    [GlobalCleanup]
    public void Cleanup()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
