using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Data.Sqlite;

/// <summary>
/// Connection and write-path tuning for the SQLite store. Bound from configuration in
/// the composition root; the database path is derived from the collector data directory.
/// </summary>
public sealed class SqliteDataOptions
{
    public const string SectionName = "Data";

    /// <summary>Absolute path to the database file. Required.</summary>
    [Required]
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>How long a blocked connection waits on a lock before returning SQLITE_BUSY.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Rows per insert transaction (CLAUDE.md / PHASE_01: default 500).</summary>
    [Range(1, 100_000)]
    public int InsertBatchSize { get; set; } = 500;

    /// <summary>
    /// Page cache size in KiB (negative <c>cache_size</c> form). Larger helps read-heavy
    /// query workloads; the default is deliberately modest for the idle-memory target.
    /// </summary>
    [Range(1_000, 1_048_576)]
    public int PageCacheKib { get; set; } = 16_384;

    /// <summary>
    /// WAL auto-checkpoint threshold in pages. The default (1,000 ≈ 4 MB) checkpoints so
    /// often under load that every commit pays a main-database fsync; 10,000 ≈ 40 MB keeps
    /// the WAL bounded while letting sustained writes run several times faster. The
    /// background maintainer also runs a passive checkpoint each tick.
    /// </summary>
    [Range(1_000, 100_000)]
    public int WalAutoCheckpointPages { get; set; } = 10_000;

    /// <summary>Max rows deleted per retention pass, keeping the writer lock under ~100 ms.</summary>
    [Range(100, 1_000_000)]
    public int RetentionDeleteChunk { get; set; } = 5_000;

    /// <summary>How often the background search-index maintainer copies new rows into FTS.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan SearchIndexInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows the maintainer indexes per pass before yielding.</summary>
    [Range(1_000, 500_000)]
    public int SearchIndexBatchSize { get; set; } = 20_000;
}
