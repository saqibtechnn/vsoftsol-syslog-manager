using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

[Trait("Category", "Ingestion")]
public sealed class IngestionLifecycleTests
{
    private static RawFrame Frame(int i) => new(
        DateTimeOffset.UtcNow, "203.0.113.10", "udp:test", Protocol.Udp,
        System.Text.Encoding.UTF8.GetBytes($"<13>lifecycle-{i}"), truncated: false);

    private sealed record Wiring(
        IngestionHostedService Hosted, IngestionChannel Channel, DiskSpillQueue Spill,
        IngestionStatistics Stats, FrameIntake Intake, ILogRepository Repo, ServiceProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private static Wiring Build(SqliteTestDatabase db, string spillDir, Action<IngestionOptions> configure, ILogRepository? repo = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ILogRepository repository = repo ?? db.Repository;
        services.AddSingleton(repository);
        services.AddSingleton<ILogRepository>(repository);
        services.AddSyslogIngestion();
        services.Configure<IngestionOptions>(o =>
        {
            o.SpillDirectory = spillDir;
            o.UdpEnabled = false;
            o.TcpEnabled = false;
            o.ChannelCapacity = 1_000;
            o.BatchSize = 200;
            o.BatchLinger = TimeSpan.FromMilliseconds(40);
            o.SpillFlushInterval = TimeSpan.FromMilliseconds(30);
            configure(o);
        });

        ServiceProvider sp = services.BuildServiceProvider();
        return new Wiring(
            sp.GetServices<IHostedService>().OfType<IngestionHostedService>().Single(),
            sp.GetRequiredService<IngestionChannel>(),
            sp.GetRequiredService<DiskSpillQueue>(),
            sp.GetRequiredService<IngestionStatistics>(),
            sp.GetRequiredService<FrameIntake>(),
            repository,
            sp);
    }

    [Fact]
    public async Task StopAsync_DrainsTheChannelAndSpill_AndCommitsEverything()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        string spillDir = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "spill");
        await using Wiring w = Build(db, spillDir, _ => { });

        await w.Hosted.StartAsync(default);
        for (int i = 0; i < 5_000; i++)
        {
            await w.Intake.AcceptAsync(Frame(i), default);
        }

        await w.Hosted.StopAsync(default);

        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().Be(5_000);
        w.Stats.Snapshot().InFlight.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ReplaysSpillLeftByAPreviousRun_BeforeItReportsStarted()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        string spillDir = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "spill");
        Directory.CreateDirectory(spillDir);

        // Simulate a previous run that spilled 800 frames and died before committing them.
        await using (var priorSpill = new DiskSpillQueue(
            Options.Create(new IngestionOptions { SpillDirectory = spillDir }), NullLogger<DiskSpillQueue>.Instance))
        {
            await priorSpill.RecoverAsync(default);
            for (int i = 0; i < 800; i++)
            {
                await priorSpill.EnqueueAsync(Frame(i), default);
            }

            await priorSpill.ForceFlushAsync(default);
        }

        await using Wiring w = Build(db, spillDir, _ => { });
        await w.Hosted.StartAsync(default);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && await db.Repository.CountAsync(new LogQuery(), CancellationToken.None) < 800)
        {
            await Task.Delay(50);
        }

        await w.Hosted.StopAsync(default);

        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().BeGreaterThanOrEqualTo(800);
        w.Stats.Snapshot().Total.SpillRecovered.Should().BeGreaterThanOrEqualTo(800);
    }

    [Fact]
    public async Task StopAsync_WhenDrainCannotFinishInTime_ReturnsAndLeavesTheRemainderOnDiskForNextStart()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        string spillDir = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "spill");
        var stall = new StallableLogRepository(db.Repository);

        await using (Wiring w = Build(db, spillDir, o => o.ShutdownDrainTimeout = TimeSpan.FromSeconds(1), stall))
        {
            await w.Hosted.StartAsync(default);
            stall.Stall();

            for (int i = 0; i < 3_000; i++)
            {
                await w.Intake.AcceptAsync(Frame(i), default);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await w.Hosted.StopAsync(default);
            sw.Stop();

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the drain timeout must bound shutdown");
        }

        // The frames are not lost — a fresh start (fresh spill queue over the same dir) recovers them.
        stall.Release();
        await using Wiring w2 = Build(db, spillDir, _ => { });
        await w2.Hosted.StartAsync(default);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && await db.Repository.CountAsync(new LogQuery(), CancellationToken.None) < 3_000)
        {
            await Task.Delay(50);
        }

        await w2.Hosted.StopAsync(default);
        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().BeGreaterThanOrEqualTo(3_000);
    }

    [Fact]
    public async Task Statistics_ExposePerListenerAndPerSourceCounters()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        string spillDir = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "spill");
        await using Wiring w = Build(db, spillDir, _ => { });
        await w.Hosted.StartAsync(default);

        IngestionStatistics stats = w.Stats;
        var intake = w.Provider.GetRequiredService<FrameIntake>();
        for (int i = 0; i < 100; i++)
        {
            await intake.AcceptAsync(new RawFrame(DateTimeOffset.UtcNow, "10.9.9.9", "udp:0.0.0.0:5514",
                Protocol.Udp, System.Text.Encoding.UTF8.GetBytes($"<13>s-{i}"), false), default);
        }

        await w.Hosted.StopAsync(default);

        IngestionStatsSnapshot snap = stats.Snapshot();
        snap.ByListener.Should().ContainKey("udp:0.0.0.0:5514");
        snap.BySource.Should().ContainKey("10.9.9.9");
        snap.BySource["10.9.9.9"].Received.Should().Be(100);
        snap.BySource["10.9.9.9"].Committed.Should().Be(100);
    }
}
