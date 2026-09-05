using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Runs database migrations and the idempotent seed before anything else starts. Being
/// the first registered <see cref="IHostedService"/>, it completes before the collector
/// host begins (CLAUDE.md working method: the database is real before ingestion runs).
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly MigrationRunner _migrations;
    private readonly DatabaseSeeder _seeder;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        SqliteConnectionFactory factory,
        MigrationRunner migrations,
        DatabaseSeeder seeder,
        ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _migrations = migrations;
        _seeder = seeder;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_factory.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logger.LogInformation("Initialising database at {DatabasePath}.", _factory.DatabasePath);
        await _migrations.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
