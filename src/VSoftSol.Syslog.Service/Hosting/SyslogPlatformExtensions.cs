using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data;
using VSoftSol.Syslog.Data.Sqlite;

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

        // IAuthenticationProvider implementation lands in Phase 4.
        // services.AddScoped<IAuthenticationProvider, LocalAuthenticationProvider>();

        return services;
    }

    /// <summary>Registers the collector background service. The Web host does not call this.</summary>
    public static IServiceCollection AddCollectorRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<CollectorHostedService>();
        return services;
    }

    internal static string ResolveDataDirectory(this IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<CollectorOptions>>().Value.DataDirectory;
}
