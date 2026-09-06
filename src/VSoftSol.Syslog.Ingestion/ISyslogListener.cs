using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// A network listener that turns received bytes into <see cref="RawFrame"/>s and hands
/// them to <see cref="FrameIntake"/>. Started and stopped by <c>IngestionHostedService</c>;
/// <see cref="StopAsync"/> stops accepting new data but does not touch the queue.
/// </summary>
public interface ISyslogListener : IAsyncDisposable
{
    /// <summary>Stable name used as the per-listener counter key, e.g. <c>udp:0.0.0.0:514</c>.</summary>
    string Name { get; }

    /// <summary>Transport this listener serves.</summary>
    Protocol Protocol { get; }

    /// <summary>The port actually bound (may differ from the configured port when it was 0).</summary>
    int BoundPort { get; }

    /// <summary>Binds the socket and begins receiving. Returns once the socket is listening.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops accepting new data and closes the socket.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
