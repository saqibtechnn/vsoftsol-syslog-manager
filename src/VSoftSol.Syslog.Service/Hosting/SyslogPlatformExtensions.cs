using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // Seams (CLAUDE.md "Two seams only"). Implementations land in Phases 1 and 4;
        // registering them here now keeps the composition root the single source of truth.
        // services.AddSingleton<ILogRepository, SqliteLogRepository>();
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
