namespace VSoftSol.Syslog.Core.Reports;

/// <summary>How a scheduled report is delivered (PHASE_10 build item 8).</summary>
public enum ReportDeliveryType
{
    /// <summary>Not scheduled — generate on demand only.</summary>
    None = 0,

    Email = 1,

    /// <summary>Written to a local directory or UNC path.</summary>
    Folder = 2,
}
