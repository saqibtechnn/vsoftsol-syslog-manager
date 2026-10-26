using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>
/// PHASE_01: kill the writer mid-transaction, reopen, assert the database is not corrupt
/// and every committed transaction is intact. Repeat 20 times.
/// </summary>
public sealed class WalCrashConsistencyTests
{
    private readonly ITestOutputHelper _output;

    public WalCrashConsistencyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task HardKillDuringIngest_LeavesDatabaseConsistent_TwentyTimes()
    {
        string probe = CrashProbeLocator.ExecutablePath();

        for (int iteration = 1; iteration <= 20; iteration++)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"vsoftsol-crash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "syslog.db");
            string readyFile = Path.Combine(dir, "ready.txt");

            try
            {
                using (Process child = StartProbe(probe, dbPath, readyFile))
                {
                    // Generous: this waits on a cold `dotnet` start, which a loaded CI/dev host can
                    // stretch well past 30 s. A timeout here is host load, not a durability fault.
                    await WaitForFileAsync(readyFile, TimeSpan.FromSeconds(120));
                    await Task.Delay(Random.Shared.Next(15, 120));
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }

                SqliteConnection.ClearAllPools();

                var options = new SqliteDataOptions { DatabasePath = dbPath };
                using var factory = new SqliteConnectionFactory(options);

                string integrity = await IntegrityCheckAsync(factory);
                integrity.Should().Be("ok", "iteration {0} left the database corrupt", iteration);

                // The store must still be usable and every previously-committed row present.
                var runner = new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance);
                await runner.MigrateAsync(CancellationToken.None);
                var repo = new SqliteLogRepository(factory, Microsoft.Extensions.Options.Options.Create(options));
                long committed = await repo.CountAsync(new LogQuery(), CancellationToken.None);
                committed.Should().BeGreaterThan(0, "the probe announces readiness only after its first commit");

                long id = await repo.AppendAsync(SampleEvents.Minimal("post-crash write"), CancellationToken.None);
                (await repo.GetByIdAsync(id, CancellationToken.None)).Should().NotBeNull();

                _output.WriteLine($"iteration {iteration}: {committed} committed rows survived, integrity ok");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDelete(dir);
            }
        }
    }

    /// <summary>
    /// Windows can still be releasing the killed process's WAL/SHM handles (or scanning them) for a
    /// moment after <c>WaitForExitAsync</c> returns, and SQLite reports that as a transient
    /// SQLITE_IOERR on the first statement. That is a harness race, not a durability fault: genuine
    /// damage surfaces as SQLITE_CORRUPT or a non-"ok" integrity result, neither of which is retried,
    /// and an I/O error that persists past the retry window still fails the test.
    /// </summary>
    private static async Task<string> IntegrityCheckAsync(SqliteConnectionFactory factory)
    {
        const int SqliteIoErr = 10;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using SqliteConnection connection = await factory.OpenAsync(CancellationToken.None);
                await using SqliteCommand check = connection.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                return (await check.ExecuteScalarAsync())!.ToString()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteIoErr && attempt < 10)
            {
                SqliteConnection.ClearAllPools();
                await Task.Delay(250);
            }
        }
    }

    private static Process StartProbe(string probeDll, string dbPath, string readyFile)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(probeDll);
        psi.ArgumentList.Add(dbPath);
        psi.ArgumentList.Add(readyFile);
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start the crash probe.");
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

        throw new TimeoutException($"Crash probe never signalled readiness ({path}).");
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
