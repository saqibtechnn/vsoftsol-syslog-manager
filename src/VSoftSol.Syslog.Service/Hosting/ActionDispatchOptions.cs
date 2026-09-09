namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>Tunables for <see cref="ActionDispatchService"/> (bound from <c>Rules:Dispatch</c>).</summary>
public sealed class ActionDispatchOptions
{
    public const string SectionName = "Rules:Dispatch";

    /// <summary>Idle poll interval when the outbox is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Rows claimed per pass.</summary>
    public int ClaimBatchSize { get; set; } = 50;

    /// <summary>Concurrent action executions. A slow action consumes one slot, never the ingest thread.</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>Attempts before a failing action is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Exponential back-off base: attempt N waits <c>Base * 2^(N-1)</c>, capped at 30 min.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A <c>running</c> row older than this is assumed abandoned (crashed dispatcher) and re-queued.</summary>
    public TimeSpan StaleRunningAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Completed / dead rows older than this are purged.</summary>
    public TimeSpan PurgeCompletedAfter { get; set; } = TimeSpan.FromDays(3);

    /// <summary>How often rule hit counters are flushed to the database.</summary>
    public TimeSpan HitFlushInterval { get; set; } = TimeSpan.FromSeconds(15);
}
