using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.Ingestion.Patterns;

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
        services.AddOptions<ParsingOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<TlsOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<SnmpOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<WinEventLogOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IngestionStatistics>();
        services.TryAddSingleton<ListenerHealthRegistry>();
        services.TryAddSingleton<IngestionChannel>();
        services.TryAddSingleton<PerSourceRateLimiter>();
        services.TryAddSingleton<DiskSpillQueue>();
        services.TryAddSingleton<FrameIntake>();

        // Parsing (Phase 3): RFC parsers, runtime-loaded vendor packs, the facade, dedup.
        services.TryAddSingleton<Rfc5424Parser>();
        services.TryAddSingleton<Rfc3164Parser>();
        services.TryAddSingleton<PatternPackLoader>();
        services.TryAddSingleton<VendorExtractor>();
        services.TryAddSingleton<MessageParser>();
        services.TryAddSingleton<DeduplicationWindow>();

        services.TryAddSingleton<IngestionPipeline>();

        services.AddSingleton<ISyslogListener, UdpSyslogListener>();
        services.AddSingleton<ISyslogListener, TcpSyslogListener>();

        // TLS/SNMP/Windows Event Log listeners need a certificate/community/API-key
        // resolver only the Service composition root can build (they reach the Data-layer
        // secret and key stores) — registered by AddCollectorRuntime, not here, so this
        // method stays usable on its own (e.g. a narrower test harness building just
        // Udp/Tcp) without requiring those delegates to exist.

        services.AddHostedService<IngestionHostedService>();

        return services;
    }
}
