using System.Diagnostics;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Benchmarks;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Streams;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.Rules.Streams;

// `--ingest-probe [--novendor] [--sequential] [--streams N] [--frames N]` : a warmed-up,
// single-shot end-to-end drain-rate probe (frame -> parse -> [enrich] -> batch -> commit
// through the real channel + spill + SQLite). Reports msg/sec after a warm-up run so JIT
// and pack compilation are not in the measured window. `--streams N` seeds N active streams
// and runs the real Phase 6 StreamRouter on every message. Everything else runs the
// BenchmarkDotNet suite.
if (args.Contains("--ingest-probe"))
{
    bool vendor = !args.Contains("--novendor");
    bool seq = args.Contains("--sequential");

    // `--streams N` (default 0): wire the Phase 6 stream router as an ingest enricher so the
    // probe measures per-message stream evaluation cost. N is the number of *active* streams.
    int streamCount = 0;
    int si = Array.IndexOf(args, "--streams");
    if (si >= 0 && si + 1 < args.Length)
    {
        streamCount = int.Parse(args[si + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    int fi = Array.IndexOf(args, "--frames");
    if (fi >= 0 && fi + 1 < args.Length)
    {
        IngestProbe.FrameCountOverride = int.Parse(args[fi + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    Console.Error.WriteLine($"[probe] start vendor={vendor} seq={seq} streams={streamCount}");
    await IngestProbe.RunAsync(warmup: true, vendorExtraction: vendor, sequential: seq, streamCount: streamCount);
    Console.Error.WriteLine("[probe] warmup done");
    await IngestProbe.RunAsync(warmup: false, vendorExtraction: vendor, sequential: seq, streamCount: streamCount);
    Console.Error.WriteLine("[probe] measure done");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(PriorityBenchmark).Assembly).Run(args);

internal static class IngestProbe
{
    internal static int FrameCountOverride;

    private static int FrameCount => FrameCountOverride > 0 ? FrameCountOverride : 200_000;

    public static async Task RunAsync(bool warmup, bool vendorExtraction, bool sequential = false, int streamCount = 0)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-ingestprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string spillDir = Path.Combine(dir, "spill");

        var data = new SqliteDataOptions { DatabasePath = Path.Combine(dir, "syslog.db"), InsertBatchSize = 2_000 };
        using var factory = new SqliteConnectionFactory(data);
        await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);
        var repo = new SqliteLogRepository(factory, Options.Create(data));

        IOptions<IngestionOptions> io = Options.Create(new IngestionOptions
        {
            SpillDirectory = spillDir,
            UdpEnabled = false,
            TcpEnabled = false,
            ChannelCapacity = 50_000,
            BatchSize = 2_000,
            BatchLinger = TimeSpan.FromMilliseconds(25),
            SpillFlushInterval = TimeSpan.FromMilliseconds(100),
        });

        var stats = new IngestionStatistics();
        var channel = new IngestionChannel(io);
        var spill = new DiskSpillQueue(io, NullLogger<DiskSpillQueue>.Instance);
        var rl = new PerSourceRateLimiter(io, TimeProvider.System);
        var intake = new FrameIntake(channel, spill, rl, stats, io, NullLogger<FrameIntake>.Instance, TimeProvider.System);
        (MessageParser parser, DeduplicationWindow dedup) =
            ParsingComposition.Build(new ParsingOptions { VendorExtractionEnabled = vendorExtraction });
        EventEnricher? enricher = null;
        if (streamCount > 0)
        {
            // Seed the seven default streams, then add operator streams up to streamCount, so
            // event_streams FK targets are real rows — exactly what StreamRouterProvider sees.
            await new DatabaseSeeder(factory, NullLogger<DatabaseSeeder>.Instance).SeedAsync(CancellationToken.None);
            var streamStore = new SqliteStreamStore(factory);
            IReadOnlyList<StreamRow> seeded = await streamStore.ListActiveAsync(CancellationToken.None);
            foreach ((string name, ConditionGroup match) in SyntheticStreams(streamCount - seeded.Count))
            {
                await streamStore.CreateAsync(name, null, match, "benchmark", CancellationToken.None);
            }

            IReadOnlyList<StreamRow> activeRows = await streamStore.ListActiveAsync(CancellationToken.None);
            StreamRouter router = StreamRouter.Build(activeRows.Select(r =>
                new StreamDefinition(r.StreamId, r.Name, r.Enabled, r.IsCatchAll, r.Match)));
            Console.Error.WriteLine($"[probe] router: {router.StreamCount} streams, {router.CompileErrors.Count} compile errors");
            enricher = (parsed, _) => ValueTask.FromResult(parsed.WithRouting(null, router.Route(parsed)));
        }

        var pipeline = new IngestionPipeline(channel, spill, repo, parser, dedup, stats, io, NullLogger<IngestionPipeline>.Instance, enricher);
        await spill.RecoverAsync(CancellationToken.None);

        var payloads = new byte[FrameCount][];
        for (int i = 0; i < FrameCount; i++)
        {
            payloads[i] = System.Text.Encoding.UTF8.GetBytes(
                $"<190>{i} %LINK-3-UPDOWN: Interface GigabitEthernet0/{i % 48}, changed state to down");
        }

        Stopwatch sw;
        if (sequential)
        {
            // Isolate the pump: fill the channel + spill queue first, then time the drain.
            for (int i = 0; i < FrameCount; i++)
            {
                await intake.AcceptAsync(
                    new RawFrame(DateTimeOffset.UtcNow, "198.51.100." + (i % 254 + 1), "probe", Protocol.Udp, payloads[i], false),
                    CancellationToken.None);
            }

            channel.Complete();
            sw = Stopwatch.StartNew();
            await pipeline.RunAsync(CancellationToken.None);
            sw.Stop();
        }
        else
        {
            var pump = Task.Run(() => pipeline.RunAsync(CancellationToken.None));
            sw = Stopwatch.StartNew();
            for (int i = 0; i < FrameCount; i++)
            {
                await intake.AcceptAsync(
                    new RawFrame(DateTimeOffset.UtcNow, "198.51.100." + (i % 254 + 1), "probe", Protocol.Udp, payloads[i], false),
                    CancellationToken.None);
            }

            channel.Complete();
            await pump;
            sw.Stop();
        }

        long rows = await repo.CountAsync(new LogQuery(), CancellationToken.None);
        double perSecond = FrameCount / sw.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"{(warmup ? "warmup " : "MEASURE")} vendor={vendorExtraction} streams={streamCount} : {FrameCount} frames, {rows} rows, " +
            $"{sw.Elapsed.TotalSeconds:F2}s => {perSecond:N0} msg/sec");
        Console.Out.Flush();

        await spill.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// <paramref name="extra"/> synthetic operator streams (OR-of-substrings, like the ones
    /// an operator adds on top of the seven seeded defaults). Negative or zero yields none.
    /// </summary>
    private static IEnumerable<(string Name, ConditionGroup Match)> SyntheticStreams(int extra)
    {
        string[] needles = ["error", "warning", "critical", "notice", "restart", "timeout", "expired", "reject", "drop", "block", "alloc", "quota"];
        for (int k = 0; k < extra; k++)
        {
            var group = new ConditionGroup
            {
                Join = ConditionJoin.Or,
                Children =
                {
                    new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = needles[k % needles.Length] },
                    new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = needles[(k + 3) % needles.Length] },
                    new ConditionComparison { Field = "hostname", Operator = ConditionOperator.StartsWith, Value = "core" },
                },
            };
            yield return ($"Operator Stream {k + 1}", group);
        }
    }
}
