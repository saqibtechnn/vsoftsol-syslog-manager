using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>Parser and extractor tuning, bound from the <c>Parsing</c> configuration section.</summary>
public sealed class ParsingOptions
{
    public const string SectionName = "Parsing";

    /// <summary>Character cap on the decoded message. Overflow is recorded as the
    /// <c>truncated</c> field, never silently cut.</summary>
    [Range(256, 4 * 1024 * 1024)]
    public int MaxMessageChars { get; set; } = 64 * 1024;

    /// <summary>Deduplication window (PHASE_03 item 8). Identical message + same host within
    /// this window increments <c>occurrence_count</c> instead of inserting a row.
    /// <c>00:00:00</c> disables it (the default).</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.Zero;

    /// <summary>Upper bound on extracted fields per message (memory-exhaustion guard).</summary>
    [Range(1, 100_000)]
    public int MaxFieldsPerMessage { get; set; } = 250;

    /// <summary>Upper bound on a single extracted field value's length.</summary>
    [Range(16, 1024 * 1024)]
    public int MaxFieldValueLength { get; set; } = 8 * 1024;

    /// <summary>Mandatory match timeout on every user-authorable pattern (ReDoS guard).</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:05")]
    public TimeSpan RegexTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Directory holding the runtime-loaded vendor parser packs. Empty =
    /// <c>&lt;base directory&gt;/Patterns</c>.</summary>
    public string PatternsDirectory { get; set; } = string.Empty;

    /// <summary>Run the vendor parser packs (per-message field extraction). Disable to keep
    /// only RFC-header parsing on a very high-volume node that does its enrichment later.</summary>
    public bool VendorExtractionEnabled { get; set; } = true;
}
