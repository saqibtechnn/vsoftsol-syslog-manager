using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>
/// PHASE_02 kill test: send at a steady rate over TCP, <see cref="Process.Kill()"/> the
/// ingest host mid-run, restart it, and assert every frame that was durable before the
/// kill (committed to the database, or fsync'd into the spill queue) is present after
/// recovery, with the database intact. The spill pipeline is deliberately slowed so a
/// large backlog is on disk at kill time.
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class KillRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public Task HardKillMidIngest_LosesNoDurableFrame_ThreeTimes() => RunAsync(3);

    [Fact]
    [Trait("Category", "Soak")]
    public Task HardKillMidIngest_LosesNoDurableFrame_TenTimes() => RunAsync(10);

    private async Task RunAsync(int iterations)
    {
        string probe = IngestionProbeLocator.DllPath();

        for (int i = 1; i <= iterations; i++)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"vsoftsol-killrec-{Guid.NewGuid():N}");
            string spillDir = Path.Combine(dir, "spill");
            Directory.CreateDirectory(spillDir);
            string dbPath = Path.Combine(dir, "syslog.db");
            string statusFile = Path.Combine(dir, "status.txt");

            try
            {
                (long received, long committed, long durable, int port) lastStatus;

                // The probe binds an ephemeral port and reports it, so there is no
                // free-port race between iterations.
                using (Process run = StartProbe(probe, "run", dbPath, spillDir, "0", statusFile, "40", "500"))
                {
                    await WaitForFileAsync(statusFile, TimeSpan.FromSeconds(45));
                    int port = await WaitForPortAsync(statusFile, TimeSpan.FromSeconds(30));

                    using var sender = new CancellationTokenSource();
                    Task feed = FeedTcpAsync(port, sender.Token);

                    // Let the pipeline commit some, and the spill accumulate a durable backlog
                    // (the pipeline is slowed below the feed rate so the spill queue fills).
                    try
                    {
                        await WaitForAsync(async () =>
                        {
                            (long r, long c, long d, int p) = await ReadStatusAsync(statusFile);
                            return c > 80 && d > 60;
                        }, TimeSpan.FromSeconds(120));
                    }
                    catch (TimeoutException)
                    {
                        string err;
                        lock (_probeErr)
                        {
                            err = _probeErr.ToString();
                        }

                        throw new TimeoutException(
                            $"iteration {i}: probe did not reach the commit/spill backlog. status='{await SafeReadAsync(statusFile)}' probe-stderr:\n{err}");
                    }

                    lastStatus = await ReadStatusAsync(statusFile);
                    await sender.CancelAsync();
                    try
                    {
                        await feed;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (SocketException)
                    {
                    }
                    catch (IOException)
                    {
                    }

                    run.Kill(entireProcessTree: true);
                    await run.WaitForExitAsync();
                }

                SqliteConnection.ClearAllPools();

                using (Process recover = StartProbe(probe, "recover", dbPath, spillDir, statusFile))
                {
                    await recover.WaitForExitAsync();
                    recover.ExitCode.Should().Be(0, "recovery run should complete cleanly");
                }

                SqliteConnection.ClearAllPools();

                long finalRows = await CountRowsAsync(dbPath);
                (await IntegrityAsync(dbPath)).Should().Be("ok", "iteration {0} left the database corrupt", i);

                long durableFloor = lastStatus.committed + lastStatus.durable;
                finalRows.Should().BeGreaterThanOrEqualTo(durableFloor,
                    "iteration {0}: every committed + fsync'd-to-spill frame must survive the kill (committed={1}, spillDurable={2})",
                    i, lastStatus.committed, lastStatus.durable);

                output.WriteLine(
                    $"iteration {i}: before kill received={lastStatus.received} committed={lastStatus.committed} " +
                    $"spillDurable={lastStatus.durable}; after recovery rows={finalRows}, integrity ok");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDelete(dir);
            }
        }
    }

    private static async Task FeedTcpAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        await using NetworkStream stream = client.GetStream();
        long n = 0;
        while (!ct.IsCancellationRequested)
        {
            var buffer = new MemoryStream();
            for (int i = 0; i < 250; i++)
            {
                byte[] msg = Encoding.UTF8.GetBytes($"<13>kill-recovery {n++}");
                buffer.Write(Encoding.ASCII.GetBytes($"{msg.Length} "));
                buffer.Write(msg);
            }

            await stream.WriteAsync(buffer.ToArray(), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(20, ct); // ~12,500 msg/sec — outruns the slowed pipeline so the spill fills
        }
    }

    private readonly System.Text.StringBuilder _probeErr = new();

    private Process StartProbe(string dll, params string[] probeArgs)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in probeArgs)
        {
            psi.ArgumentList.Add(a);
        }

        Process p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the ingestion probe.");

        // Drain the pipes so a full buffer never deadlocks the probe.
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (_probeErr) { _probeErr.AppendLine(e.Data); } } };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        return p;
    }

    private static async Task<(long Received, long Committed, long Durable, int Port)> ReadStatusAsync(string path)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string[] parts = (await reader.ReadToEndAsync()).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return (long.Parse(parts[0], CultureInfo.InvariantCulture),
                        long.Parse(parts[1], CultureInfo.InvariantCulture),
                        long.Parse(parts[2], CultureInfo.InvariantCulture),
                        parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : 0);
            }
            catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException)
            {
                await Task.Delay(20);
            }
        }

        throw new IOException($"Could not read probe status {path}.");
    }

    private static async Task<int> WaitForPortAsync(string statusFile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            (long _, long _, long _, int port) = await ReadStatusAsync(statusFile);
            if (port > 0)
            {
                return port;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Probe never reported its bound port.");
    }

    private static async Task<long> CountRowsAsync(string dbPath)
    {
        var options = new SqliteDataOptions { DatabasePath = dbPath };
        using var factory = new SqliteConnectionFactory(options);
        var repo = new SqliteLogRepository(factory, Microsoft.Extensions.Options.Options.Create(options));
        return await repo.CountAsync(new LogQuery(), CancellationToken.None);
    }

    private static async Task<string> IntegrityAsync(string dbPath)
    {
        var options = new SqliteDataOptions { DatabasePath = dbPath };
        using var factory = new SqliteConnectionFactory(options);
        await using SqliteConnection connection = await factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        return (await check.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<string> SafeReadAsync(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return (await reader.ReadToEndAsync()).Trim();
        }
        catch (IOException)
        {
            return "<unreadable>";
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Probe never wrote its status file ({path}).");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition not met before timeout.");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
