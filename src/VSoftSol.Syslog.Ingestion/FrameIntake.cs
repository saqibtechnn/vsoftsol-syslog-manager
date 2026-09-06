using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// The one path every received frame takes: rate-limit decision → in-memory channel →
/// (on a full channel) disk spill queue. Keeps the "never lose a message" accounting in a
/// single place so both listeners behave identically. A frame is only ever discarded here
/// for an explicit, counted reason: an over-ceiling source under Drop/Quarantine, the
/// spill queue at its hard cap, or a zero-length datagram.
/// </summary>
public sealed class FrameIntake
{
    private readonly IngestionChannel _channel;
    private readonly DiskSpillQueue _spill;
    private readonly PerSourceRateLimiter _rateLimiter;
    private readonly IngestionStatistics _stats;
    private readonly IngestionOptions _options;
    private readonly ILogger<FrameIntake> _logger;
    private readonly TimeProvider _time;
    private long _lastSpillFullLogTicks;

    public FrameIntake(
        IngestionChannel channel,
        DiskSpillQueue spill,
        PerSourceRateLimiter rateLimiter,
        IngestionStatistics stats,
        IOptions<IngestionOptions> options,
        ILogger<FrameIntake> logger,
        TimeProvider? timeProvider = null)
    {
        _channel = channel;
        _spill = spill;
        _rateLimiter = rateLimiter;
        _stats = stats;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Accepts one frame off a listener. Never throws for hostile input.</summary>
    public async ValueTask AcceptAsync(RawFrame frame, CancellationToken cancellationToken)
    {
        IngestionStatistics.MutableCounters listener = _stats.Listener(frame.ListenerName);
        IngestionStatistics.MutableCounters source = _stats.Source(frame.SourceIp);
        listener.AddReceived();
        source.AddReceived();

        if (frame.Payload.Length == 0)
        {
            listener.AddDroppedEmpty();
            source.AddDroppedEmpty();
            return;
        }

        switch (_rateLimiter.Check(frame.SourceIp))
        {
            case PerSourceRateLimiter.Decision.Drop:
                listener.AddDroppedRateLimited();
                source.AddDroppedRateLimited();
                _stats.SetQuarantinedSources(_rateLimiter.QuarantinedCount);
                return;

            case PerSourceRateLimiter.Decision.Throttle:
                listener.AddThrottled();
                source.AddThrottled();
                if (_options.ThrottleDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(_options.ThrottleDelay, _time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                break;
        }

        if (_channel.TryWrite(frame))
        {
            listener.AddQueued();
            source.AddQueued();
        }
        else if (await _spill.EnqueueAsync(frame, cancellationToken).ConfigureAwait(false))
        {
            listener.AddSpilled();
            source.AddSpilled();
        }
        else
        {
            listener.AddDroppedSpillFull();
            source.AddDroppedSpillFull();
            WarnSpillFull();
        }

        _stats.SetChannel(_channel.Depth, _channel.Capacity);
        _stats.SetSpill(_spill.PendingFrameCount, _spill.ByteDepth);
    }

    private void WarnSpillFull()
    {
        long now = _time.GetTimestamp();
        long last = Interlocked.Read(ref _lastSpillFullLogTicks);
        if (_time.GetElapsedTime(last, now) < TimeSpan.FromSeconds(5)
            && last != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSpillFullLogTicks, now);
        _logger.LogError(
            "Disk spill queue is at its {Cap}-byte cap; frames are being dropped. Free disk space or raise Ingestion:SpillMaxBytes.",
            _options.SpillMaxBytes);
    }
}
