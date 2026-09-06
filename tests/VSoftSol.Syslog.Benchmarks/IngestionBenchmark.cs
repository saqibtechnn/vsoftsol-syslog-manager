using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Benchmarks;

/// <summary>
/// End-to-end ingest drain rate (frame received → parsed → committed) through the real
/// channel + spill + pipeline path — the number compared across Phases 2/3/6/7/12
/// (TESTING_STANDARDS.md §5). Unpaced burst; three iterations, mean and standard
/// deviation. Per-frame latency in <c>ingest-latency.txt</c> is backlog-dominated in
/// burst mode — steady-state parse latency is in <c>ParseBenchmark</c>.
/// </summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class IngestionBenchmark
{
    private const int FrameCount = 200_000;

    private byte[][] _payloads = [];
    private string _dir = string.Empty;
    private SqliteConnectionFactory? _factory;
    private LatencyRecordingRepository? _repo;
    private IngestionChannel? _channel;
    private DiskSpillQueue? _spill;
    private FrameIntake? _intake;
    private IngestionPipeline? _pipeline;

    [GlobalSetup]
    public void BuildPayloads()
    {
        _payloads = new byte[FrameCount][];
        for (int i = 0; i < FrameCount; i++)
        {
            _payloads[i] = System.Text.Encoding.UTF8.GetBytes(
                $"<190>{i} %LINK-3-UPDOWN: Interface GigabitEthernet0/{i % 48}, changed state to down");
        }
    }

    [IterationSetup]
    public void Wire()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsoftsol-ingbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        string spillDir = Path.Combine(_dir, "spill");

        var data = new SqliteDataOptions { DatabasePath = Path.Combine(_dir, "syslog.db"), InsertBatchSize = 1_000 };
        _factory = new SqliteConnectionFactory(data);
        new MigrationRunner(_factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        _repo = new LatencyRecordingRepository(new SqliteLogRepository(_factory, Options.Create(data)));

        IOptions<IngestionOptions> io = Options.Create(new IngestionOptions
        {
            SpillDirectory = spillDir,
            UdpEnabled = false,
            TcpEnabled = false,
            ChannelCapacity = 50_000,
            BatchSize = 2_000,
            BatchLinger = TimeSpan.FromMilliseconds(50),
            SpillFlushInterval = TimeSpan.FromMilliseconds(100),
        });

        var stats = new IngestionStatistics();
        _channel = new IngestionChannel(io);
        _spill = new DiskSpillQueue(io, NullLogger<DiskSpillQueue>.Instance);
        var rl = new PerSourceRateLimiter(io, TimeProvider.System);
        _intake = new FrameIntake(_channel, _spill, rl, stats, io, NullLogger<FrameIntake>.Instance, TimeProvider.System);
        (MessageParser parser, DeduplicationWindow dedup) = ParsingComposition.Build();
        _pipeline = new IngestionPipeline(_channel, _spill, _repo, parser, dedup, stats, io, NullLogger<IngestionPipeline>.Instance);
        _spill.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void Teardown()
    {
        _repo?.DumpPercentiles(Path.Combine(RepoRoot(), "docs", "evidence", "phase-02", "ingest-latency.txt"), FrameCount);
        _spill?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VSoftSol.Syslog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Benchmark(OperationsPerInvoke = FrameCount)]
    public async Task<long> EndToEndIngest()
    {
        var cts = new CancellationTokenSource();
        Task pump = Task.Run(() => _pipeline!.RunAsync(cts.Token));

        // Unpaced burst: push everything as fast as possible and measure the drain rate
        // (parse + batch + commit). This is the number compared across Phases 2/3/6/7/12.
        for (int i = 0; i < FrameCount; i++)
        {
            var frame = new RawFrame(DateTimeOffset.UtcNow, "198.51.100." + (i % 254 + 1), "bench",
                Protocol.Udp, _payloads[i], truncated: false);
            await _intake!.AcceptAsync(frame, CancellationToken.None);
        }

        _channel!.Complete();
        await pump;
        return _repo!.CommittedCount;
    }

    private sealed class LatencyRecordingRepository(ILogRepository inner) : ILogRepository
    {
        private readonly List<double> _latenciesMs = new(FrameCount);
        private long _committed;

        public long CommittedCount => Interlocked.Read(ref _committed);

        public async Task<IReadOnlyList<long>> AppendBatchAsync(IReadOnlyCollection<SyslogEvent> events, CancellationToken cancellationToken)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_latenciesMs)
            {
                foreach (SyslogEvent e in events)
                {
                    _latenciesMs.Add((now - e.ReceivedUtc).TotalMilliseconds);
                }
            }

            IReadOnlyList<long> ids = await inner.AppendBatchAsync(events, cancellationToken);
            Interlocked.Add(ref _committed, events.Count);
            return ids;
        }

        public async Task<long> AppendAsync(SyslogEvent syslogEvent, CancellationToken cancellationToken) =>
            (await AppendBatchAsync([syslogEvent], cancellationToken))[0];

        public Task<SyslogEvent?> GetByIdAsync(long eventId, CancellationToken cancellationToken) => inner.GetByIdAsync(eventId, cancellationToken);

        public Task IncrementOccurrenceAsync(IReadOnlyDictionary<long, int> increments, CancellationToken cancellationToken) =>
            inner.IncrementOccurrenceAsync(increments, cancellationToken);

        public IAsyncEnumerable<SyslogEvent> QueryAsync(LogQuery query, CancellationToken cancellationToken) => inner.QueryAsync(query, cancellationToken);

        public Task<long> CountAsync(LogQuery query, CancellationToken cancellationToken) => inner.CountAsync(query, cancellationToken);

        public Task<IReadOnlyList<SyslogEvent>> GetContextAsync(long eventId, int before, int after, CancellationToken cancellationToken) =>
            inner.GetContextAsync(eventId, before, after, cancellationToken);

        public void DumpPercentiles(string path, int frameCount)
        {
            double[] sorted;
            lock (_latenciesMs)
            {
                sorted = _latenciesMs.ToArray();
            }

            if (sorted.Length == 0)
            {
                return;
            }

            Array.Sort(sorted);
            double P(double q) => sorted[(int)Math.Clamp(q * (sorted.Length - 1), 0, sorted.Length - 1)];
            string line = string.Create(CultureInfo.InvariantCulture,
                $"frames={frameCount} committed={sorted.Length} latency_ms p50={P(0.50):F1} p95={P(0.95):F1} p99={P(0.99):F1} max={sorted[^1]:F1}");
            File.AppendAllText(path, line + Environment.NewLine);
            Console.WriteLine(line);
        }
    }
}
