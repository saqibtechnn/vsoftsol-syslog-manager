namespace VSoftSol.Syslog.Ingestion;

/// <summary>An immutable point-in-time reading of the ingestion counters.</summary>
public sealed record IngestionStatsSnapshot
{
    public required DateTimeOffset TakenUtc { get; init; }

    /// <summary>Current in-memory channel depth.</summary>
    public required int ChannelDepth { get; init; }

    /// <summary>Current channel capacity.</summary>
    public required int ChannelCapacity { get; init; }

    /// <summary>Frames currently held on disk in the spill queue, not yet committed.</summary>
    public required long SpillFrameCount { get; init; }

    /// <summary>Bytes currently on disk in the spill queue.</summary>
    public required long SpillBytes { get; init; }

    /// <summary>Open TCP connections.</summary>
    public required int ActiveTcpConnections { get; init; }

    /// <summary>Sources currently quarantined by the rate limiter.</summary>
    public required int QuarantinedSources { get; init; }

    public required IReadOnlyDictionary<string, CounterSet> ByListener { get; init; }

    public required IReadOnlyDictionary<string, CounterSet> BySource { get; init; }

    /// <summary>Sum of <see cref="ByListener"/>.</summary>
    public CounterSet Total { get; init; } = new();

    /// <summary>
    /// The accounting identity every load test checks: <c>Received == Committed + Dropped +
    /// Failed + (in flight)</c>. "In flight" is what is still in the channel or spill queue.
    /// </summary>
    public long InFlight => Total.Received - Total.Committed - Total.Dropped - Total.Failed;
}

/// <summary>One set of ingestion counters (per listener, per source, or the total).</summary>
public sealed record CounterSet
{
    /// <summary>Frames taken off the wire.</summary>
    public long Received { get; init; }

    /// <summary>Frames handed to the in-memory channel (fast path).</summary>
    public long Queued { get; init; }

    /// <summary>Frames written to the disk spill queue because the channel was full.</summary>
    public long Spilled { get; init; }

    /// <summary>Frames read back out of the spill queue and re-queued (includes startup replay).</summary>
    public long SpillRecovered { get; init; }

    /// <summary>Frames durably committed to the event store.</summary>
    public long Committed { get; init; }

    /// <summary>Frames discarded by the rate limiter (Drop / Quarantine behaviour only).</summary>
    public long DroppedRateLimited { get; init; }

    /// <summary>Frames from an over-ceiling source that were delayed but still queued
    /// (Throttle behaviour). Not a loss — an observability signal.</summary>
    public long Throttled { get; init; }

    /// <summary>Frames discarded because the spill queue hit its hard size cap.</summary>
    public long DroppedSpillFull { get; init; }

    /// <summary>Zero-length or otherwise unusable frames discarded at the listener.</summary>
    public long DroppedEmpty { get; init; }

    /// <summary>Frames that reached the writer but a commit attempt threw (retried, then counted).</summary>
    public long Failed { get; init; }

    /// <summary>All discard reasons combined.</summary>
    public long Dropped => DroppedRateLimited + DroppedSpillFull + DroppedEmpty;
}
