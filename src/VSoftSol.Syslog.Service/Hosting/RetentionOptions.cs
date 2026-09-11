namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>Tunables for the retention tiering scheduler (<c>Retention</c> config section;
/// PHASE_10 build item 2).</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>How often the scheduler wakes to run one round of tiering/verification.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Archive restores expired per tick.</summary>
    public int RestoreExpiryBatchSize { get; set; } = 50;

    /// <summary>Archives whose Delete horizon has passed, purged per tick.</summary>
    public int PurgeBatchSize { get; set; } = 50;

    /// <summary>Archives re-hashed per tick.</summary>
    public int VerificationBatchSize { get; set; } = 20;

    /// <summary>An archive is re-verified once it has gone this long without a check.</summary>
    public TimeSpan VerificationStaleAfter { get; set; } = TimeSpan.FromDays(7);
}
