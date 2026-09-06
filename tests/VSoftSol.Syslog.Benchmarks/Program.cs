using System.Diagnostics;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Benchmarks;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;

// `--ingest-probe [--novendor]` : a warmed-up, single-shot end-to-end drain-rate probe
// (frame -> parse -> batch -> commit through the real channel + spill + SQLite). Reports
// msg/sec after a warm-up run so JIT and pack compilation are not in the measured window.
// Everything else runs the BenchmarkDotNet suite.
if (args.Contains("--ingest-probe"))
{
    bool vendor = !args.Contains("--novendor");
    bool seq = args.Contains("--sequential");
    await IngestProbe.RunAsync(warmup: true, vendorExtraction: vendor, sequential: seq);
    await IngestProbe.RunAsync(warmup: false, vendorExtraction: vendor, sequential: seq);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(PriorityBenchmark).Assembly).Run(args);

internal static class IngestProbe
{
    private const int FrameCount = 200_000;

    public static async Task RunAsync(bool warmup, bool vendorExtraction, bool sequential = false)
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
        var pipeline = new IngestionPipeline(channel, spill, repo, parser, dedup, stats, io, NullLogger<IngestionPipeline>.Instance);
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
            $"{(warmup ? "warmup " : "MEASURE")} vendor={vendorExtraction} : {FrameCount} frames, {rows} rows, " +
            $"{sw.Elapsed.TotalSeconds:F2}s => {perSecond:N0} msg/sec");

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
}
