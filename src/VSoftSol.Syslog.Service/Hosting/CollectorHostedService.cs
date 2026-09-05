using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The collector's long-running background service. Phase 0: a no-op that proves the
/// generic-host wiring. Listeners, the ingest pipeline, and the rules engine attach
/// here in later phases.
/// </summary>
public sealed class CollectorHostedService : BackgroundService
{
    private readonly ILogger<CollectorHostedService> _logger;
    private readonly CollectorOptions _options;

    public CollectorHostedService(ILogger<CollectorHostedService> logger, IOptions<CollectorOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {

        _logger.LogInformation(
            "Collector host started. Data directory {DataDirectory}. No listeners in this build.",
            _options.DataDirectory);

        // Phase 0 has nothing to run. Return a task that completes only on shutdown so
        // the host does not treat this as a crashed service.
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Collector host stopping.");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
