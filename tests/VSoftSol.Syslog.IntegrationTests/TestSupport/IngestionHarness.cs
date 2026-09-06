using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Wires a real ingest path — <see cref="FrameIntake"/> → <see cref="IngestionChannel"/> /
/// <see cref="DiskSpillQueue"/> → <see cref="IngestionPipeline"/> → a real SQLite
/// repository — over throwaway temp directories. Listeners are optional and added by the
/// test that needs them.
/// </summary>
public sealed class IngestionHarness : IAsyncDisposable
{
    private readonly string _spillDir;
    private CancellationTokenSource? _pipelineCts;
    private Task _pipelineTask = Task.CompletedTask;
    private readonly List<ISyslogListener> _listeners = [];

    private IngestionHarness(
        SqliteTestDatabase db, IngestionOptions options, ParsingOptions parsingOptions, ILogRepository repo, TimeProvider time)
    {
        Db = db;
        Options = options;
        ParsingOptions = parsingOptions;
        _spillDir = options.SpillDirectory;
        Repository = repo;
        Stats = new IngestionStatistics();
        Channel = new IngestionChannel(Wrap(options));
        RateLimiter = new PerSourceRateLimiter(Wrap(options), time);
        Spill = new DiskSpillQueue(Wrap(options), NullLogger<DiskSpillQueue>.Instance);
        Intake = new FrameIntake(Channel, Spill, RateLimiter, Stats, Wrap(options),
            NullLogger<FrameIntake>.Instance, time);
        (Parser, Dedup) = ParsingComposition.Build(parsingOptions, timeProvider: time);
        Pipeline = new IngestionPipeline(Channel, Spill, Repository, Parser, Dedup, Stats, Wrap(options),
            NullLogger<IngestionPipeline>.Instance);
        Time = time;
    }

    public ParsingOptions ParsingOptions { get; }

    public MessageParser Parser { get; }

    public DeduplicationWindow Dedup { get; }

    public SqliteTestDatabase Db { get; }

    public IngestionOptions Options { get; }

    public ILogRepository Repository { get; }

    public IngestionStatistics Stats { get; }

    public IngestionChannel Channel { get; }

    public DiskSpillQueue Spill { get; }

    public PerSourceRateLimiter RateLimiter { get; }

    public FrameIntake Intake { get; }

    public IngestionPipeline Pipeline { get; }

    public TimeProvider Time { get; }

    public string SpillDirectory => _spillDir;

    public static async Task<IngestionHarness> CreateAsync(
        Action<IngestionOptions>? configure = null,
        Func<ILogRepository, ILogRepository>? decorateRepository = null,
        TimeProvider? timeProvider = null,
        Action<ParsingOptions>? configureParsing = null)
    {
        SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await db.Seeder.SeedAsync(CancellationToken.None); // the "Parse Failures" stream
        string spillDir = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "spill");

        var options = new IngestionOptions
        {
            SpillDirectory = spillDir,
            UdpEnabled = false,
            TcpEnabled = false,
            ChannelCapacity = 2_000,
            BatchSize = 500,
            BatchLinger = TimeSpan.FromMilliseconds(50),
            SpillFlushInterval = TimeSpan.FromMilliseconds(40),
            ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
        };
        configure?.Invoke(options);

        var parsing = new ParsingOptions();
        configureParsing?.Invoke(parsing);

        ILogRepository repo = decorateRepository is null ? db.Repository : decorateRepository(db.Repository);
        var harness = new IngestionHarness(db, options, parsing, repo, timeProvider ?? TimeProvider.System);
        await harness.Spill.RecoverAsync(CancellationToken.None);
        return harness;
    }

    private static IOptions<IngestionOptions> Wrap(IngestionOptions o) =>
        Microsoft.Extensions.Options.Options.Create(o);

    public void StartPipeline()
    {
        _pipelineCts = new CancellationTokenSource();
        _pipelineTask = Task.Run(() => Pipeline.RunAsync(_pipelineCts.Token));
    }

    /// <summary>Signals no more input and waits for the pipeline to drain everything.</summary>
    public async Task DrainAsync(TimeSpan? timeout = null)
    {
        Channel.Complete();
        await _pipelineTask.WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
    }

    /// <summary>Cancels the pipeline immediately (simulates a hard stop mid-drain).</summary>
    public async Task HardStopPipelineAsync()
    {
        if (_pipelineCts is not null)
        {
            await _pipelineCts.CancelAsync();
            try
            {
                await _pipelineTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void AddListener(ISyslogListener listener) => _listeners.Add(listener);

    public UdpSyslogListener AddUdpListener(Action<IngestionOptions>? tweak = null)
    {
        tweak?.Invoke(Options);
        var l = new UdpSyslogListener(Intake, Wrap(Options), NullLogger<UdpSyslogListener>.Instance);
        _listeners.Add(l);
        return l;
    }

    public TcpSyslogListener AddTcpListener(Action<IngestionOptions>? tweak = null)
    {
        tweak?.Invoke(Options);
        var l = new TcpSyslogListener(Intake, Stats, Wrap(Options), NullLogger<TcpSyslogListener>.Instance);
        _listeners.Add(l);
        return l;
    }

    public Task<long> CommittedCountAsync() =>
        Db.Repository.CountAsync(new Core.Abstractions.LogQuery(), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        foreach (ISyslogListener l in _listeners)
        {
            await l.DisposeAsync();
        }

        await HardStopPipelineAsync();
        _pipelineCts?.Dispose();
        await Spill.DisposeAsync();
        await Db.DisposeAsync();
    }
}
