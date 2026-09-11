namespace VSoftSol.Syslog.Core.Reports;

/// <summary>Where a scheduled report's output goes (PHASE_10 build item 8).</summary>
public sealed record ReportDeliveryConfig
{
    public ReportDeliveryType Type { get; init; } = ReportDeliveryType.None;

    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>Local directory or UNC path — required when <see cref="Type"/> is Folder.</summary>
    public string? FolderPath { get; init; }

    public static ReportDeliveryConfig None { get; } = new();
}
