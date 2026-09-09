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

    // `--rules N` (default 0): seed N active rules and run the real Phase 7 rule engine on
    // every message (evaluation + outbox enqueue, but not action execution — that is off the
    // ingest thread by design; RuleIngestIsolationTests proves it).
    int ruleCount = 0;
    int ri = Array.IndexOf(args, "--rules");
    if (ri >= 0 && ri + 1 < args.Length)
    {
        ruleCount = int.Parse(args[ri + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    Console.Error.WriteLine($"[probe] start vendor={vendor} seq={seq} streams={streamCount} rules={ruleCount}");
    await IngestProbe.RunAsync(warmup: true, vendorExtraction: vendor, sequential: seq, streamCount: streamCount, ruleCount: ruleCount);
    Console.Error.WriteLine("[probe] warmup done");
    await IngestProbe.RunAsync(warmup: false, vendorExtraction: vendor, sequential: seq, streamCount: streamCount, ruleCount: ruleCount);
    Console.Error.WriteLine("[probe] measure done");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(PriorityBenchmark).Assembly).Run(args);

internal static class IngestProbe
{
    internal static int FrameCountOverride;

    private static int FrameCount => FrameCountOverride > 0 ? FrameCountOverride : 200_000;

    public static async Task RunAsync(
        bool warmup, bool vendorExtraction, bool sequential = false, int streamCount = 0, int ruleCount = 0)
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

        if (ruleCount > 0)
        {
            EventEnricher ruleEnricher = await BuildRuleEnricherAsync(factory, ruleCount);
            EventEnricher? prior = enricher;
            enricher = prior is null
                ? ruleEnricher
                : async (parsed, ct) => await ruleEnricher(await prior(parsed, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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
    /// <summary>
    /// Seeds <paramref name="ruleCount"/> active rules (OR-of-substrings filters + a notify
    /// action) and returns an enricher that runs the real Phase 7 rule engine — matching +
    /// throttle + outbox enqueue — on every message, exactly as the collector host does.
    /// </summary>
    private static async Task<EventEnricher> BuildRuleEnricherAsync(SqliteConnectionFactory factory, int ruleCount)
    {
        var store = new VSoftSol.Syslog.Data.Rules.SqliteRuleStore(factory);
        string[] needles = ["error", "warning", "critical", "denied", "failure", "down", "restart", "timeout", "expired", "reject"];
        for (int i = 0; i < ruleCount; i++)
        {
            var filter = new ConditionGroup
            {
                Join = ConditionJoin.Or,
                Children =
                {
                    new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = needles[i % needles.Length] },
                    new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = needles[(i + 3) % needles.Length] },
                    new ConditionComparison { Field = "severity", Operator = ConditionOperator.LessThan, Value = "3" },
                },
            };
            await store.CreateAsync(new VSoftSol.Syslog.Core.Rules.RuleDefinition
            {
                Name = $"bench rule {i}",
                Priority = i,
                Filter = filter,
                Actions = [new VSoftSol.Syslog.Core.Rules.RaiseNotificationAction { Title = "hit on {hostname}" }],
            }, "benchmark", CancellationToken.None);
        }

        var provider = new VSoftSol.Syslog.Service.Hosting.RuleSetProvider(store, NullLogger<VSoftSol.Syslog.Service.Hosting.RuleSetProvider>.Instance);
        var runtime = new VSoftSol.Syslog.Rules.Rules.RuleRuntime(TimeProvider.System, new VSoftSol.Syslog.Rules.Rules.RuleRuntimeOptions());
        VSoftSol.Syslog.Rules.Rules.RuleSet set = await provider.GetAsync(CancellationToken.None);
        Console.Error.WriteLine($"[probe] rules: {set.RuleCount} active, {set.CompileErrors.Count} compile errors");

        return (parsed, _) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyList<VSoftSol.Syslog.Rules.Rules.CompiledRule> matched = set.Match(parsed, [], now);
            if (matched.Count == 0)
            {
                return ValueTask.FromResult(parsed);
            }

            VSoftSol.Syslog.Rules.Rules.RuleOutcome outcome = runtime.Apply(matched, parsed);
            if (!outcome.HasWork)
            {
                return ValueTask.FromResult(parsed);
            }

            var pending = outcome.Dispatches.Select(d => new VSoftSol.Syslog.Core.Rules.PendingRuleAction(
                d.RuleId, d.RuleName, d.ActionIndex,
                VSoftSol.Syslog.Core.Rules.RuleActionInfo.Kind(d.Action),
                VSoftSol.Syslog.Data.Rules.RuleJson.SerializeAction(d.Action), d.WasEscalation)).ToList();
            return ValueTask.FromResult(parsed.WithRuleOutcome(outcome.Tags, outcome.ExtraStreamIds, pending));
        };
    }

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
