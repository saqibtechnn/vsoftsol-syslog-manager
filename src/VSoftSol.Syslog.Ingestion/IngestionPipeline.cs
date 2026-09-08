using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Drains the in-memory channel and the disk spill queue, parses each frame
/// (<see cref="MessageParser"/>: RFC 5424 → RFC 3164 → raw), applies the deduplication
/// window, batches, and commits through <see cref="ILogRepository"/> (PHASE_02 item 5,
/// PHASE_03 items 3 &amp; 8). Spill segments advance only after their frames are committed,
/// and a commit failure spills the in-memory frames back to disk rather than dropping them,
/// so the pipeline cannot lose an accepted message.
/// </summary>
public sealed class IngestionPipeline
{
    private readonly IngestionChannel _channel;
    private readonly DiskSpillQueue _spill;
    private readonly ILogRepository _repository;
    private readonly MessageParser _parser;
    private readonly DeduplicationWindow _dedup;
    private readonly IngestionStatistics _stats;
    private readonly IngestionOptions _options;
    private readonly ILogger<IngestionPipeline> _logger;
    private readonly EventEnricher? _enricher;

    public IngestionPipeline(
        IngestionChannel channel,
        DiskSpillQueue spill,
        ILogRepository repository,
        MessageParser parser,
        DeduplicationWindow dedup,
        IngestionStatistics stats,
        IOptions<IngestionOptions> options,
        ILogger<IngestionPipeline> logger,
        EventEnricher? enricher = null)
    {
        _channel = channel;
        _spill = spill;
        _repository = repository;
        _parser = parser;
        _dedup = dedup;
        _stats = stats;
        _options = options.Value;
        _logger = logger;
        _enricher = enricher;
    }

    /// <summary>
    /// Runs until <paramref name="stoppingToken"/> fires <em>and</em> the channel is
    /// complete and the spill queue is empty. A hard stop mid-drain leaves the remainder
    /// on disk for the next start.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Ingestion pipeline started (batch {Batch}, linger {Linger}).", _options.BatchSize, _options.BatchLinger);

        var channelFrames = new List<RawFrame>(_options.BatchSize);

        while (true)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                // The graceful-drain window (IngestionHostedService.ShutdownDrainTimeout)
                // has elapsed. Flush whatever is still in memory to the durable spill queue
                // and exit — it is recovered on the next start, never lost.
                long flushed = await DrainChannelToSpillAsync().ConfigureAwait(false);
                if (flushed > 0)
                {
                    _logger.LogWarning("Shutdown drain window exceeded; {Count} in-memory frame(s) written to the spill queue.", flushed);
                }

                break;
            }

            channelFrames.Clear();
            DrainChannel(channelFrames, _options.BatchSize);

            DiskSpillQueue.SpillLease? lease = null;
            if (channelFrames.Count < _options.BatchSize && _spill.PendingFrameCount > 0)
            {
                lease = await _spill.LeaseAsync(_options.BatchSize - channelFrames.Count, stoppingToken).ConfigureAwait(false);
            }

            if (channelFrames.Count == 0 && (lease is null || lease.Frames.Count == 0))
            {
                if (lease is not null)
                {
                    // Empty step-over lease: advance past an exhausted segment.
                    await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                bool more;
                try
                {
                    more = await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue; // re-enter the loop; the cancellation branch at the top handles the drain
                }

                if (!more && _spill.IsEmpty)
                {
                    break;
                }

                continue;
            }

            await CommitBatchAsync(channelFrames, lease, stoppingToken).ConfigureAwait(false);
            _stats.SetChannel(_channel.Depth, _channel.Capacity);
            _stats.SetSpill(_spill.PendingFrameCount, _spill.ByteDepth);
        }

        _logger.LogInformation("Ingestion pipeline drained and stopped.");
    }

    private async Task<long> DrainChannelToSpillAsync()
    {
        long flushed = 0;
        while (_channel.Reader.TryRead(out RawFrame? frame))
        {
            if (await _spill.EnqueueAsync(frame, CancellationToken.None).ConfigureAwait(false))
            {
                _stats.Listener(frame.ListenerName).AddSpilled();
                _stats.Source(frame.SourceIp).AddSpilled();
                flushed++;
            }
            else
            {
                _stats.Listener(frame.ListenerName).AddFailed(1);
                _stats.Source(frame.SourceIp).AddFailed(1);
            }
        }

        await _spill.ForceFlushAsync(CancellationToken.None).ConfigureAwait(false);
        return flushed;
    }

    private void DrainChannel(List<RawFrame> into, int max)
    {
        while (into.Count < max && _channel.Reader.TryRead(out RawFrame? frame))
        {
            into.Add(frame);
        }
    }

    private async Task<bool> WaitForWorkAsync(CancellationToken stoppingToken)
    {
        // Wake on new channel data, or periodically to re-check the spill queue.
        using var timer = new CancellationTokenSource(_options.BatchLinger);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timer.Token);
        try
        {
            return await _channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            return true; // linger elapsed — loop again to check the spill queue
        }
    }

    private async Task CommitBatchAsync(
        List<RawFrame> channelFrames, DiskSpillQueue.SpillLease? lease, CancellationToken stoppingToken)
    {
        int total = channelFrames.Count + (lease?.Frames.Count ?? 0);

        // Parse every frame, then fold same-host/same-body repeats within the dedup window
        // into an occurrence bump on the already-committed event instead of a new row.
        var toInsert = new List<SyslogEvent>(total);
        var insertKeys = new List<string>(total);
        Dictionary<long, int>? dupIncrements = null;

        void Prepare(RawFrame f)
        {
            SyslogEvent e = _parser.Parse(f);
            if (_dedup.Enabled)
            {
                string key = MessageParser.DeduplicationKey(e);
                long? existing = _dedup.LookupRecent(key);
                if (existing is { } id)
                {
                    dupIncrements ??= [];
                    dupIncrements[id] = dupIncrements.GetValueOrDefault(id) + 1;
                    return;
                }

                insertKeys.Add(key);
            }
            else
            {
                insertKeys.Add(string.Empty);
            }

            toInsert.Add(e);
        }

        foreach (RawFrame f in channelFrames)
        {
            Prepare(f);
        }

        if (lease is not null)
        {
            foreach (RawFrame f in lease.Frames)
            {
                Prepare(f);
            }
        }

        // Attach device resolution + stream routing (PHASE_06). Routing failures are
        // absorbed here rather than propagated — an unroutable event still commits (it
        // will always at least land in the catch-all via the repository safety net).
        if (_enricher is not null)
        {
            for (int i = 0; i < toInsert.Count; i++)
            {
                try
                {
                    toInsert[i] = await _enricher(toInsert[i], stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Event enrichment failed for a message from {SourceIp}; committing unrouted.",
                        toInsert[i].SourceIp);
                }
            }
        }

        int attempt = 0;
        while (true)
        {
            try
            {
                if (toInsert.Count > 0)
                {
                    IReadOnlyList<long> ids = await _repository.AppendBatchAsync(toInsert, stoppingToken).ConfigureAwait(false);
                    if (_dedup.Enabled)
                    {
                        for (int k = 0; k < ids.Count; k++)
                        {
                            _dedup.Record(insertKeys[k], ids[k]);
                        }
                    }
                }

                if (dupIncrements is { Count: > 0 })
                {
                    await _repository.IncrementOccurrenceAsync(dupIncrements, stoppingToken).ConfigureAwait(false);
                }

                CountCommitted(channelFrames);
                if (lease is not null)
                {
                    CountCommitted(lease.Frames);
                    foreach (RawFrame f in lease.Frames)
                    {
                        _stats.Listener(f.ListenerName).AddSpillRecovered(1);
                        _stats.Source(f.SourceIp).AddSpillRecovered(1);
                    }

                    await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown during a commit — do not retry against a possibly-dead writer.
                // Push the in-memory frames to disk; the lease stays uncommitted and is
                // re-leased on the next start.
                await SpillBackAsync(channelFrames).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(
                    ex, "Commit of {Count} event(s) failed (attempt {Attempt}); will retry.", total, attempt);

                if (attempt >= 5)
                {
                    await SpillBackAsync(channelFrames).ConfigureAwait(false);
                    // The lease is left uncommitted; its frames are re-leased next pass.
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await SpillBackAsync(channelFrames).ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    private async Task SpillBackAsync(List<RawFrame> frames)
    {
        foreach (RawFrame f in frames)
        {
            if (!await _spill.EnqueueAsync(f, CancellationToken.None).ConfigureAwait(false))
            {
                _stats.Listener(f.ListenerName).AddFailed(1);
                _stats.Source(f.SourceIp).AddFailed(1);
            }
        }

        if (frames.Count > 0)
        {
            _logger.LogWarning("{Count} in-memory frame(s) spilled to disk after repeated commit failures.", frames.Count);
        }
    }

    private void CountCommitted(IReadOnlyCollection<RawFrame> frames)
    {
        foreach (RawFrame f in frames)
        {
            _stats.Listener(f.ListenerName).AddCommitted(1);
            _stats.Source(f.SourceIp).AddCommitted(1);
        }
    }
}
