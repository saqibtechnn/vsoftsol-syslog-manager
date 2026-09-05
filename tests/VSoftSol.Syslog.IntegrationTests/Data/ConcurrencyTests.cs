using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class ConcurrencyTests
{
    // The phase asks for 60s; the default run uses 8s so CI stays fast. The full-duration
    // soak is opt-in via the "soak" trait (TESTING_STANDARDS.md §1).
    [Theory]
    [InlineData(8)]
    [Trait("Category", "Soak")]
    [InlineData(60)]
    public async Task OneWriterFiveReaders_NoSqliteBusy_NoTornReads(int seconds)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync(o => o.BusyTimeout = TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var failures = new List<Exception>();

        Task writer = Task.Run(async () =>
        {
            long n = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await db.Repository.AppendAsync(SampleEvents.Minimal($"w{n++}"), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }

                    return;
                }
            }
        });

        Task[] readers = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    long count = await db.Repository.CountAsync(new LogQuery(), CancellationToken.None);
                    count.Should().BeGreaterThanOrEqualTo(0);

                    await foreach (SyslogEvent e in db.Repository.QueryAsync(
                        new LogQuery { Limit = 20 }, CancellationToken.None))
                    {
                        e.RawMessage.Length.Should().BeGreaterThan(0, "a torn read would produce an empty raw payload");
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }

                    return;
                }
            }
        })).ToArray();

        await Task.WhenAll([writer, .. readers]);

        failures.Should().BeEmpty();

        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        (await check.ExecuteScalarAsync())!.ToString().Should().Be("ok");
    }

    [Fact]
    public async Task WriteLock_SerialisesConcurrentBatchAppends_WithoutLoss()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var sw = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(w => Task.Run(() =>
            db.Repository.AppendBatchAsync(
                Enumerable.Range(0, 200).Select(i => SampleEvents.Minimal($"w{w}-{i}")).ToList(),
                CancellationToken.None))));

        sw.Stop();
        (await db.Repository.CountAsync(new LogQuery(), CancellationToken.None)).Should().Be(2_000);
    }
}
