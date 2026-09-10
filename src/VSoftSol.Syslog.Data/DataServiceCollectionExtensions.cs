using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Streams;
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
        services.AddOptions<SearchOptions>().ValidateDataAnnotations();
        services.TryAddSingleton<ScopedEventReader>();
        services.TryAddSingleton<SqliteSavedSearchStore>();
        services.TryAddSingleton<SqliteColumnLayoutStore>();
        services.TryAddSingleton<SqliteSearchFacets>();
        services.TryAddSingleton<SqliteExtractorStore>();

        // Phase 6 — device registry, discovery, groups, stream routing.
        services.TryAddSingleton<SqliteDeviceStore>();
        services.TryAddSingleton<SqliteDeviceGroupStore>();
        services.TryAddSingleton<SqliteDeviceMetrics>();
        services.TryAddSingleton<SqliteDiscoverySettingsStore>();
        services.TryAddSingleton<SqliteStreamStore>();
        services.TryAddSingleton<DeviceResolver>();
        services.TryAddSingleton<SqliteAuditLog>();

        // Phase 7 — rules engine, action outbox, notification centre.
        services.TryAddSingleton<Rules.SqliteRuleStore>();
        services.TryAddSingleton<Rules.SqliteActionOutbox>();
        services.TryAddSingleton<Notifications.SqliteNotificationStore>();

        // Phase 8 — aggregation alerts: definitions, instance lifecycle, window reader, action outbox.
        services.TryAddSingleton<Alerts.SqliteAlertStore>();
        services.TryAddSingleton<Alerts.SqliteAlertInstanceStore>();
        services.TryAddSingleton<Alerts.SqliteAlertWindowReader>();
        services.TryAddSingleton<Alerts.SqliteAlertActionOutbox>();
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecretProtector, DpapiSecretProtector>();
        }

        services.TryAddSingleton<SqliteSecretStore>();

        // Phase 9 — dashboards: definition store, aggregation reader, collector-stat series.
        // AggregationCache is registered by the composition root (it binds its TTL to the
        // Dashboards options section).
        services.TryAddSingleton<Dashboards.SqliteDashboardStore>();
        services.TryAddSingleton<Dashboards.SqliteAggregationReader>();
        services.TryAddSingleton<Dashboards.SystemSeriesReader>();

        services.AddHostedService<SearchIndexMaintainer>();

        return services;
    }
}
