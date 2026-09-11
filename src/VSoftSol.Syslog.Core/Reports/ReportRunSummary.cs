namespace VSoftSol.Syslog.Core.Reports;

public enum ReportRunStatus
{
    Running = 0,
    Ok = 1,
    Failed = 2,
}

/// <summary>One execution of a report — on-demand or scheduled (PHASE_10 build item 8).</summary>
public sealed record ReportRunSummary
{
    public long RunId { get; init; }

    public long ReportId { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public ReportRunStatus Status { get; init; } = ReportRunStatus.Running;

    public int? RowCount { get; init; }

    public string? PdfPath { get; init; }

    public string? CsvPath { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset? DeliveredUtc { get; init; }

    public string? DeliveryError { get; init; }

    /// <summary>Username, or "scheduler" for an automatic run.</summary>
    public required string TriggeredBy { get; init; }
}
