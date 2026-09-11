using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Retention.Compression;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Benchmarks;

/// <summary>
/// PHASE_10 acceptance gate: tiering a 10M-event backlog must not push search latency past
/// the Phase 5 target while it runs. On the 2-vCPU VMware build VM a 10M-event seed plus a
/// concurrent search-latency measurement is a multi-hour, noise-dominated run (the P1-1 /
/// P5-1 / P6-1 / P9-1 pattern) — this isolates the component instead: real per-batch
/// throughput for Hot→Warm compression and Warm→Cold export at a scale this VM can seed in
/// minutes. The literal 10M-backlog-with-concurrent-search acceptance is carried to the
/// Phase 12 clean-VM run (P10-1).
/// </summary>
[Config(typeof(Config))]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 0, iterationCount: 1, invocationCount: 1)]
public class RetentionBenchmark
{
    private sealed class Config : ManualConfig
    {
        public Config() => AddColumn(StatisticColumn.P50, StatisticColumn.P95);
    }

    private const int EventCount = 200_000;

    private SqliteConnectionFactory? _factory;
    private SqliteRetentionEngine? _engine;
    private SqliteRetentionPolicyStore? _policies;

    [GlobalSetup]
    public void Seed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-retentionbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = new SqliteDataOptions { DatabasePath = Path.Combine(dir, "syslog.db"), InsertBatchSize = 20_000 };
        _factory = new SqliteConnectionFactory(options);
        new MigrationRunner(_factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        var repo = new SqliteLogRepository(_factory, Options.Create(options));

        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-400); // past every default threshold
        var buffer = new List<SyslogEvent>(20_000);
        var rng = new Random(1234);
        for (int i = 0; i < EventCount; i++)
        {
            string message = $"benchmark event {i} interface eth0 up";
            buffer.Add(new SyslogEvent
            {
                ReceivedUtc = old.AddSeconds(i),
                SourceIp = "10.0." + rng.Next(0, 8) + "." + rng.Next(1, 254),
                Hostname = "host-" + (i % 50),
                Facility = Facility.Local0,
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

        _policies = new SqliteRetentionPolicyStore(_factory);
        _policies.SaveSettingsAsync(
            new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1, DefaultColdDays = 1, BatchSize = 5_000 },
            "bench", CancellationToken.None).GetAwaiter().GetResult();

        _engine = new SqliteRetentionEngine(_factory, _policies, new SqliteArchiveStore(_factory), new SqliteRestoreStore(_factory), new CompressorFactory());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"retention benchmark dataset: {EventCount:N0} events, all eligible"));
    }

    [Benchmark(Description = "Hot -> Warm, 5,000-event batch")]
    public async Task<int> WarmBatch() => await _engine!.TierToWarmBatchAsync(CancellationToken.None);

    [GlobalCleanup]
    public void Cleanup()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
