namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>Tunables for the alert scheduler and its action dispatcher (<c>Alerts</c> config section).</summary>
public sealed class AlertEvaluationOptions
{
    public const string SectionName = "Alerts";

    /// <summary>How often the scheduler wakes to check which alerts are due. The floor on evaluation granularity.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Row cap on the in-memory filtered window scan. A window with more matching rows than
    /// this is evaluated on a truncated (lower-bound) count and a diagnostic notification is
    /// raised.
    /// </summary>
    public int MaxWindowScan { get; set; } = 500_000;

    /// <summary>How many trigger event ids to record per firing.</summary>
    public int TriggerEventSample { get; set; } = 20;

    /// <summary>
    /// A run is "missed" (and logged, not skipped) if it is overdue by more than this many
    /// evaluation intervals — e.g. the process was down.
    /// </summary>
    public int MissedRunGraceMultiplier { get; set; } = 2;

    // ---- action dispatcher ----

    public TimeSpan DispatchPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int DispatchBatchSize { get; set; } = 25;

    public int DispatchParallelism { get; set; } = 3;

    public int DispatchMaxAttempts { get; set; } = 5;

    public TimeSpan DispatchBackoffBase { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StaleRunningAfter { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan PurgeCompletedAfter { get; set; } = TimeSpan.FromDays(7);
}
