namespace VSoftSol.Syslog.Ingestion.Extraction;

/// <summary>
/// One ordered stage of the extraction pipeline (PHASE_03 item 6): GROK, regex named
/// captures, key-value, JSON, lookup table, or rename/coerce/drop. A stage must never
/// throw and never hang — regex stages compile with a mandatory match timeout.
/// </summary>
public interface IExtractor
{
    /// <summary>A short name for diagnostics and evidence (e.g. <c>grok</c>, <c>kv</c>).</summary>
    string Kind { get; }

    /// <summary>Applies this stage to <paramref name="context"/>, adding/removing fields.</summary>
    void Apply(ExtractionContext context);
}
