using System.Collections.Concurrent;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Process-wide ingestion counters (PHASE_02 item 8). Every mutation is a single
/// <see cref="Interlocked"/> operation so listeners, the pipeline, and the rate limiter
/// update it without locking. <see cref="Snapshot"/> takes a consistent-enough reading for
/// the UI and for the load-test accounting checks.
/// </summary>
public sealed class IngestionStatistics
{
    private readonly ConcurrentDictionary<string, MutableCounters> _byListener = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MutableCounters> _bySource = new(StringComparer.Ordinal);

    private int _channelDepth;
    private int _channelCapacity;
    private long _spillFrames;
    private long _spillBytes;
    private int _activeTcpConnections;
    private int _quarantinedSources;

    internal MutableCounters Listener(string name) => _byListener.GetOrAdd(name, static _ => new MutableCounters());

    internal MutableCounters Source(string sourceIp) => _bySource.GetOrAdd(sourceIp, static _ => new MutableCounters());

    public void SetChannel(int depth, int capacity)
    {
        Interlocked.Exchange(ref _channelDepth, depth);
        Interlocked.Exchange(ref _channelCapacity, capacity);
    }

    public void SetSpill(long frames, long bytes)
    {
        Interlocked.Exchange(ref _spillFrames, frames);
        Interlocked.Exchange(ref _spillBytes, bytes);
    }

    public void TcpConnectionOpened() => Interlocked.Increment(ref _activeTcpConnections);

    public void TcpConnectionClosed() => Interlocked.Decrement(ref _activeTcpConnections);

    public void SetQuarantinedSources(int count) => Interlocked.Exchange(ref _quarantinedSources, count);

    public IngestionStatsSnapshot Snapshot()
    {
        Dictionary<string, CounterSet> listeners = _byListener.ToDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);
        Dictionary<string, CounterSet> sources = _bySource.ToDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);

        return new IngestionStatsSnapshot
        {
            TakenUtc = DateTimeOffset.UtcNow,
            ChannelDepth = Volatile.Read(ref _channelDepth),
            ChannelCapacity = Volatile.Read(ref _channelCapacity),
            SpillFrameCount = Interlocked.Read(ref _spillFrames),
            SpillBytes = Interlocked.Read(ref _spillBytes),
            ActiveTcpConnections = Volatile.Read(ref _activeTcpConnections),
            QuarantinedSources = Volatile.Read(ref _quarantinedSources),
            ByListener = listeners,
            BySource = sources,
            Total = Sum(listeners.Values),
        };
    }

    private static CounterSet Sum(IEnumerable<CounterSet> parts)
    {
        long received = 0, queued = 0, spilled = 0, recovered = 0, committed = 0;
        long droppedRate = 0, throttled = 0, droppedSpill = 0, droppedEmpty = 0, failed = 0;
        foreach (CounterSet p in parts)
        {
            received += p.Received;
            queued += p.Queued;
            spilled += p.Spilled;
            recovered += p.SpillRecovered;
            committed += p.Committed;
            droppedRate += p.DroppedRateLimited;
            throttled += p.Throttled;
            droppedSpill += p.DroppedSpillFull;
            droppedEmpty += p.DroppedEmpty;
            failed += p.Failed;
        }

        return new CounterSet
        {
            Received = received,
            Queued = queued,
            Spilled = spilled,
            SpillRecovered = recovered,
            Committed = committed,
            DroppedRateLimited = droppedRate,
            Throttled = throttled,
            DroppedSpillFull = droppedSpill,
            DroppedEmpty = droppedEmpty,
            Failed = failed,
        };
    }

    /// <summary>Mutable counter block; one per listener and one per source IP.</summary>
    internal sealed class MutableCounters
    {
        private long _received;
        private long _queued;
        private long _spilled;
        private long _spillRecovered;
        private long _committed;
        private long _droppedRateLimited;
        private long _throttled;
        private long _droppedSpillFull;
        private long _droppedEmpty;
        private long _failed;

        public void AddReceived() => Interlocked.Increment(ref _received);

        public void AddQueued() => Interlocked.Increment(ref _queued);

        public void AddSpilled() => Interlocked.Increment(ref _spilled);

        public void AddSpillRecovered(long n) => Interlocked.Add(ref _spillRecovered, n);

        public void AddCommitted(long n) => Interlocked.Add(ref _committed, n);

        public void AddDroppedRateLimited() => Interlocked.Increment(ref _droppedRateLimited);

        public void AddThrottled() => Interlocked.Increment(ref _throttled);

        public void AddDroppedSpillFull() => Interlocked.Increment(ref _droppedSpillFull);

        public void AddDroppedEmpty() => Interlocked.Increment(ref _droppedEmpty);

        public void AddFailed(long n) => Interlocked.Add(ref _failed, n);

        public CounterSet ToImmutable() => new()
        {
            Received = Interlocked.Read(ref _received),
            Queued = Interlocked.Read(ref _queued),
            Spilled = Interlocked.Read(ref _spilled),
            SpillRecovered = Interlocked.Read(ref _spillRecovered),
            Committed = Interlocked.Read(ref _committed),
            DroppedRateLimited = Interlocked.Read(ref _droppedRateLimited),
            Throttled = Interlocked.Read(ref _throttled),
            DroppedSpillFull = Interlocked.Read(ref _droppedSpillFull),
            DroppedEmpty = Interlocked.Read(ref _droppedEmpty),
            Failed = Interlocked.Read(ref _failed),
        };
    }
}
