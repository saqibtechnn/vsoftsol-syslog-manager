namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>Tunables for the scheduled-report engine (<c>Reports</c> config section;
/// PHASE_10 build item 8).</summary>
public sealed class ReportSchedulerOptions
{
    public const string SectionName = "Reports";

    /// <summary>How often the scheduler checks which reports are due.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Where generated PDF/CSV files are kept for the record and for download from
    /// the Web UI. Empty resolves to a subfolder of the data directory at runtime.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Immediate delivery retries within one run before giving up and raising an alert.</summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    public TimeSpan DeliveryRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
