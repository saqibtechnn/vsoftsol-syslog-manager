using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Ingestion;

// Modes:
//   run     <dbPath> <spillDir> <tcpPort> <statusFile> <commitDelayMs> <channelCap>
//   recover <dbPath> <spillDir> <statusFile>
//
// `run` receives syslog over TCP through the real ingest pipeline and rewrites <statusFile>
// every 100 ms with "<received> <committed> <spillDurable>". The parent kills it mid-run.
// `recover` re-opens the same db + spill dir, drains everything left, and exits 0.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: IngestionProbe run|recover ...");
    return 2;
}

string mode = args[0];

static SqliteDataOptions DataOptions(string dbPath) => new() { DatabasePath = dbPath, InsertBatchSize = 200 };

static IngestionOptions IngestionOptions(string spillDir, int tcpPort, int channelCap) => new()
{
    SpillDirectory = spillDir,
    UdpEnabled = false,
    TcpEnabled = tcpPort > 0,
    TcpBindAddress = "127.0.0.1",
    TcpPort = tcpPort,
    ChannelCapacity = channelCap,
    BatchSize = 200,
    BatchLinger = TimeSpan.FromMilliseconds(50),
    SpillFlushInterval = TimeSpan.FromMilliseconds(50),
};

static void WriteStatus(string path, long received, long committed, long durable, int port)
{
    // Best effort: a concurrent read from the parent can briefly lock the file. Never let
    // a status-write failure crash the probe — that would hang the test.
    string text = string.Create(CultureInfo.InvariantCulture, $"{received} {committed} {durable} {port}");
    for (int attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var w = new StreamWriter(fs);
            w.Write(text);
            return;
        }
        catch (IOException)
        {
            Thread.Sleep(10);
        }
    }
}

if (mode == "run")
{
    string dbPath = args[1];
    string spillDir = args[2];
    int tcpPort = int.Parse(args[3], CultureInfo.InvariantCulture); // 0 = pick an ephemeral port
    string statusFile = args[4];
    int commitDelayMs = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 0;
    int channelCap = args.Length > 6 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 2000;

    var dataOptions = DataOptions(dbPath);
    using var factory = new SqliteConnectionFactory(dataOptions);
    await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);

    ILogRepository repo = new DelayingRepository(
        new SqliteLogRepository(factory, Options.Create(dataOptions)), commitDelayMs);

    IOptions<IngestionOptions> io = Options.Create(IngestionOptions(spillDir, tcpPort, channelCap));
    var stats = new IngestionStatistics();
    var channel = new IngestionChannel(io);
    var rateLimiter = new PerSourceRateLimiter(io, TimeProvider.System);
    var spill = new DiskSpillQueue(io, NullLogger<DiskSpillQueue>.Instance);
    var intake = new FrameIntake(channel, spill, rateLimiter, stats, io, NullLogger<FrameIntake>.Instance, TimeProvider.System);
    var pipeline = new IngestionPipeline(channel, spill, repo, stats, io, NullLogger<IngestionPipeline>.Instance);

    await spill.RecoverAsync(CancellationToken.None);
    _ = Task.Run(() => pipeline.RunAsync(CancellationToken.None));

    var listener = new TcpSyslogListener(intake, stats, io, NullLogger<TcpSyslogListener>.Instance);
    await listener.StartAsync(CancellationToken.None);

    while (true)
    {
        IngestionStatsSnapshot s = stats.Snapshot();
        WriteStatus(statusFile, s.Total.Received, s.Total.Committed, spill.DurableFrameCount, listener.BoundPort);
        await Task.Delay(100);
    }
}

if (mode == "recover")
{
    string dbPath = args[1];
    string spillDir = args[2];
    string statusFile = args[3];

    var dataOptions = DataOptions(dbPath);
    using var factory = new SqliteConnectionFactory(dataOptions);
    await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);
    var repo = new SqliteLogRepository(factory, Options.Create(dataOptions));

    IOptions<IngestionOptions> io = Options.Create(IngestionOptions(spillDir, 0, 4000));
    var stats = new IngestionStatistics();
    var channel = new IngestionChannel(io);
    var rateLimiter = new PerSourceRateLimiter(io, TimeProvider.System);
    var spill = new DiskSpillQueue(io, NullLogger<DiskSpillQueue>.Instance);
    var pipeline = new IngestionPipeline(channel, spill, repo, stats, io, NullLogger<IngestionPipeline>.Instance);

    await spill.RecoverAsync(CancellationToken.None);
    Task pump = Task.Run(() => pipeline.RunAsync(CancellationToken.None));

    while (!spill.IsEmpty)
    {
        await Task.Delay(50);
    }

    await Task.Delay(200);
    channel.Complete();
    await pump;
    await spill.DisposeAsync();

    long committed = await repo.CountAsync(new LogQuery(), CancellationToken.None);
    WriteStatus(statusFile, 0, committed, 0, 0);
    return 0;
}

Console.Error.WriteLine($"unknown mode: {mode}");
return 2;

internal sealed class DelayingRepository(ILogRepository inner, int delayMs) : ILogRepository
{
    public async Task<long> AppendAsync(SyslogEvent syslogEvent, CancellationToken cancellationToken)
    {
        IReadOnlyList<long> ids = await AppendBatchAsync([syslogEvent], cancellationToken);
        return ids[0];
    }

    public async Task<IReadOnlyList<long>> AppendBatchAsync(IReadOnlyCollection<SyslogEvent> events, CancellationToken cancellationToken)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        return await inner.AppendBatchAsync(events, cancellationToken);
    }

    public Task<SyslogEvent?> GetByIdAsync(long eventId, CancellationToken cancellationToken) => inner.GetByIdAsync(eventId, cancellationToken);

    public IAsyncEnumerable<SyslogEvent> QueryAsync(LogQuery query, CancellationToken cancellationToken) => inner.QueryAsync(query, cancellationToken);

    public Task<long> CountAsync(LogQuery query, CancellationToken cancellationToken) => inner.CountAsync(query, cancellationToken);

    public Task<IReadOnlyList<SyslogEvent>> GetContextAsync(long eventId, int before, int after, CancellationToken cancellationToken) =>
        inner.GetContextAsync(eventId, before, after, cancellationToken);
}
