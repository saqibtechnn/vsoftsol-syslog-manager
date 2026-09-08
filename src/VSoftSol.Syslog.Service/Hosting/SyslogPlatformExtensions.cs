using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Ingestion;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The single composition root. Both the Windows Service host and the Blazor Web host
/// call <see cref="AddSyslogPlatform"/>, so there is exactly one place that wires the
/// product's services (Phase 0 item 6).
/// </summary>
public static class SyslogPlatformExtensions
{
    public static IServiceCollection AddSyslogPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CollectorOptions>()
            .Bind(configuration.GetSection(CollectorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Migrations + seed run before any other hosted service (registered first so it
        // starts first).
        services.AddHostedService<DatabaseInitializer>();

        // Data layer (Phase 1). The database path is not configurable on its own — it
        // lives under the collector data directory unless an operator overrides it in the
        // "Data" section. AddSyslogData also registers the SearchIndexMaintainer hosted
        // service, which starts after DatabaseInitializer.
        services.AddSyslogData();
        services.AddOptions<SqliteDataOptions>()
            .Bind(configuration.GetSection(SqliteDataOptions.SectionName))
            .PostConfigure<IOptions<CollectorOptions>>((data, collector) =>
            {
                if (string.IsNullOrWhiteSpace(data.DatabasePath))
                {
                    data.DatabasePath = Path.Combine(collector.Value.DataDirectory, "syslog.db");
                }
            });

        // Authentication (Phase 4). The local Argon2id provider is the v1 implementation of
        // the IAuthenticationProvider seam; an AD/LDAP provider replaces it without touching
        // the UI or the authorization layer.
        services.AddOptions<Argon2idOptions>()
            .Bind(configuration.GetSection(Argon2idOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<AuthenticationOptions>()
            .Bind(configuration.GetSection(AuthenticationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<LocalAuthenticationProvider>();
        services.TryAddSingleton<IAuthenticationProvider>(sp => sp.GetRequiredService<LocalAuthenticationProvider>());

        // Phase 6 — the stream router. Lives here because only the composition root may
        // bridge Data (the stream store) and Rules (the router). The Web host uses it too
        // (the stream tester); the collector host uses it via the ingest enricher.
        services.TryAddSingleton<StreamRouterProvider>();

        return services;
    }

    /// <summary>
    /// Registers the collector runtime — the UDP/TCP listeners, the bounded channel, the
    /// disk spill queue, and the ingest pipeline (Phase 2). The Web host does not call this,
    /// so the UI process never binds a listener.
    /// </summary>
    public static IServiceCollection AddCollectorRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSyslogIngestion();
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .PostConfigure<IOptions<CollectorOptions>>((ingestion, collector) =>
            {
                if (string.IsNullOrWhiteSpace(ingestion.SpillDirectory))
                {
                    ingestion.SpillDirectory = Path.Combine(collector.Value.DataDirectory, "spill");
                }
            });

        // The ingest enricher: resolve the source IP to a device (auto-discovering unknown
        // sources) and route the event to its streams, once, before the batch commits
        // (PHASE_06 items 3 & 6). Only the collector host runs the pipeline, so this is
        // registered here rather than in AddSyslogPlatform.
        services.TryAddSingleton<EventEnricher>(sp =>
        {
            var resolver = sp.GetRequiredService<VSoftSol.Syslog.Data.Devices.DeviceResolver>();
            var routers = sp.GetRequiredService<StreamRouterProvider>();
            return async (parsed, cancellationToken) =>
            {
                long? deviceId = await resolver.ResolveAsync(parsed.SourceIp, parsed.Hostname, cancellationToken)
                    .ConfigureAwait(false);
                VSoftSol.Syslog.Rules.Streams.StreamRouter router =
                    await routers.GetAsync(cancellationToken).ConfigureAwait(false);
                return parsed.WithRouting(deviceId, router.Route(parsed));
            };
        });

        return services;
    }

    internal static string ResolveDataDirectory(this IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<CollectorOptions>>().Value.DataDirectory;
}
