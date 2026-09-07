using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.Data;

/// <summary>DI registration for the SQLite data layer.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the connection factory, migration runner, seeder, and the
    /// <see cref="ILogRepository"/> implementation. The caller is responsible for
    /// configuring <see cref="SqliteDataOptions"/> (in particular
    /// <see cref="SqliteDataOptions.DatabasePath"/>) and for running
    /// <see cref="MigrationRunner"/> + <see cref="DatabaseSeeder"/> at startup.
    /// </summary>
    public static IServiceCollection AddSyslogData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SqliteDataOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<MigrationRunner>();
        services.TryAddSingleton<DatabaseSeeder>();
        services.TryAddSingleton<SqliteLogRepository>();
        services.TryAddSingleton<ILogRepository>(sp => sp.GetRequiredService<SqliteLogRepository>());

        // Phase 4 — authentication, scope, audit, secrets.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.TryAddSingleton<SqliteUserStore>();
        services.TryAddSingleton<SqliteSessionStore>();
        services.TryAddSingleton<ScopedEventReader>();
        services.TryAddSingleton<SqliteAuditLog>();
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecretProtector, DpapiSecretProtector>();
        }

        services.TryAddSingleton<SqliteSecretStore>();

        services.AddHostedService<SearchIndexMaintainer>();

        return services;
    }
}
