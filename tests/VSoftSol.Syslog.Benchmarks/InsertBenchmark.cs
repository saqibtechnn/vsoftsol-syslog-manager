using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Benchmarks;

/// <summary>
/// PHASE_01 gate: batched 1,000,000-row insert throughput. Must clear 20,000 rows/sec;
/// run three times, report mean and standard deviation (TESTING_STANDARDS.md §4).
/// FTS indexing is deferred (ADR 0009) so it does not sit on this path.
/// </summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class InsertBenchmark
{
    private const int RowCount = 1_000_000;

    private SyslogEvent[] _events = [];
    private string _dir = string.Empty;
    private SqliteConnectionFactory? _factory;
    private SqliteLogRepository? _repository;

    [GlobalSetup]
    public void BuildEvents()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _events = new SyslogEvent[RowCount];
        for (int i = 0; i < RowCount; i++)
        {
            string message = $"%LINK-3-UPDOWN: Interface GigabitEthernet0/{i % 48}, changed state to down seq={i}";
            _events[i] = new SyslogEvent
            {
                ReceivedUtc = baseTime.AddMilliseconds(i),
                SourceIp = "198.51.100." + (i % 254 + 1),
                Hostname = "sw-" + (i % 32),
                Facility = Facility.Local7,
                Severity = Severity.Warning,
                Protocol = Protocol.Udp,
                Message = message,
                RawMessage = System.Text.Encoding.UTF8.GetBytes(message),
                ParseStatus = ParseStatus.Rfc3164,
            };
        }
    }

    [IterationSetup]
    public void CreateDatabase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsoftsol-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var options = new SqliteDataOptions { DatabasePath = Path.Combine(_dir, "syslog.db"), InsertBatchSize = 50_000 };
        _factory = new SqliteConnectionFactory(options);
        new MigrationRunner(_factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        _repository = new SqliteLogRepository(_factory, Options.Create(options));
    }

    [IterationCleanup]
    public void DropDatabase()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark(OperationsPerInvoke = RowCount)]
    public async Task<int> BatchInsertOneMillionRows()
    {
        IReadOnlyList<long> ids = await _repository!.AppendBatchAsync(_events, CancellationToken.None);
        return ids.Count;
    }
}
