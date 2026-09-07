using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// Bounds for the search subsystem, bound from the <c>Search</c> configuration section.
/// Defaults are safe for a single-node install; an operator only touches these to raise
/// the export cap or tune live-tail cadence.
/// </summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>Hard ceiling on a single grid page, regardless of what the request asks for.</summary>
    [Range(1, 100_000)]
    public int MaxPageSize { get; set; } = 5_000;

    /// <summary>Hard ceiling on a streamed export. A larger request is capped, not refused.</summary>
    [Range(1, 100_000_000)]
    public int ExportMaxRows { get; set; } = 100_000;

    /// <summary>Maximum new rows returned by one live-tail poll.</summary>
    [Range(1, 10_000)]
    public int LiveTailBatchSize { get; set; } = 500;

    /// <summary>Maximum <c>±N</c> neighbours the context view will return on each side.</summary>
    [Range(1, 5_000)]
    public int ContextMaxNeighbours { get; set; } = 500;

    /// <summary>
    /// Count queries stop early once this many matching rows are seen and report
    /// "<c>N+</c>". Keeps the total-row indicator cheap on a huge hot window.
    /// </summary>
    [Range(1_000, 100_000_000)]
    public int ExactCountCeiling { get; set; } = 10_000;
}
