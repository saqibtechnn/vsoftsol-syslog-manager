using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

// Usage: VSoftSol.Syslog.CrashProbe <dbPath> <readyFile>
// Migrates the database, then appends committed events one at a time forever. Writes
// <readyFile> once it has committed its first row so the parent knows it is safe to kill.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CrashProbe <dbPath> <readyFile>");
    return 2;
}

string dbPath = args[0];
string readyFile = args[1];

var options = new SqliteDataOptions { DatabasePath = dbPath, InsertBatchSize = 1 };
using var factory = new SqliteConnectionFactory(options);

var runner = new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance);
await runner.MigrateAsync(CancellationToken.None);

var repository = new SqliteLogRepository(factory, Microsoft.Extensions.Options.Options.Create(options));

long i = 0;
bool announced = false;
while (true)
{
    i++;
    var evt = new SyslogEvent
    {
        ReceivedUtc = DateTimeOffset.UtcNow,
        SourceIp = "10.1.1.1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = "crash-probe row " + i.ToString(CultureInfo.InvariantCulture),
        RawMessage = System.Text.Encoding.UTF8.GetBytes("crash-probe row " + i),
        ParseStatus = ParseStatus.Raw,
    };

    await repository.AppendAsync(evt, CancellationToken.None);

    if (!announced)
    {
        await File.WriteAllTextAsync(readyFile, i.ToString(CultureInfo.InvariantCulture));
        announced = true;
    }
}
