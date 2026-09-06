namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// One stage of the parse fallback chain (RFC 5424 → RFC 3164 → raw). A parser must:
/// never throw, never hang, always return quickly, and return <c>false</c> (not a
/// partial result) when the input is not its format so the next stage can try.
/// </summary>
public interface ISyslogParser
{
    /// <summary>
    /// Attempts to parse <paramref name="text"/> (already decoded from bytes by
    /// <see cref="PayloadDecoder"/>). <paramref name="receivedUtc"/> is used to infer the
    /// year for formats that omit it.
    /// </summary>
    bool TryParse(string text, DateTimeOffset receivedUtc, out SyslogParseResult result);
}
