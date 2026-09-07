namespace VSoftSol.Syslog.Reporting.Export;

/// <summary>The output formats a search result set can be exported to (PHASE_05 item 7).</summary>
public enum ExportFormat
{
    /// <summary>RFC 4180 CSV, with spreadsheet formula-injection neutralisation applied to cells.</summary>
    Csv,

    /// <summary>A streamed JSON array, one object per event.</summary>
    Json,

    /// <summary>The verbatim <c>raw_message</c> of each event, one per line (lossy UTF-8 view).</summary>
    RawText,
}
