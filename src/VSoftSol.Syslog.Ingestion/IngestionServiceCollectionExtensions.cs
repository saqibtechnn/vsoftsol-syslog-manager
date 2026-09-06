using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>DI registration for the ingestion layer (listeners, channel, spill queue, pipeline).</summary>
public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ingest path. Requires an <c>ILogRepository</c> to already be
    /// registered (the Data layer). The caller binds <see cref="IngestionOptions"/> from
    /// configuration and sets <see cref="IngestionOptions.SpillDirectory"/>. Called by the
    /// Service composition root, never by the Web host.
    /// </summary>
    public static IServiceCollection AddSyslogIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IngestionOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IngestionStatistics>();
        services.TryAddSingleton<IngestionChannel>();
        services.TryAddSingleton<PerSourceRateLimiter>();
        services.TryAddSingleton<DiskSpillQueue>();
        services.TryAddSingleton<FrameIntake>();
        services.TryAddSingleton<IngestionPipeline>();

        services.AddSingleton<ISyslogListener, UdpSyslogListener>();
        services.AddSingleton<ISyslogListener, TcpSyslogListener>();

        services.AddHostedService<IngestionHostedService>();

        return services;
    }
}
