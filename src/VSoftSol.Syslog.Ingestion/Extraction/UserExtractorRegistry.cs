namespace VSoftSol.Syslog.Ingestion.Extraction;

/// <summary>
/// The live snapshot of operator-saved extractors (Settings → Pattern tester). Applied to
/// every ingested message regardless of matched vendor — the user-extensible half of "no
/// message is ever rejected for coming from an unrecognised vendor" (CLAUDE.md constraint
/// 4). Populated once at collector startup; same disposition as vendor <c>.pack</c> files —
/// a saved/edited/disabled extractor takes effect on the next restart, never live.
/// </summary>
public sealed class UserExtractorRegistry
{
    private volatile ExtractorPipeline _current = ExtractorPipeline.Empty;

    public ExtractorPipeline Current => _current;

    public void SetPipeline(ExtractorPipeline pipeline) => _current = pipeline;
}
