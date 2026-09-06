namespace VSoftSol.Syslog.Ingestion.Extraction;

/// <summary>
/// An ordered set of <see cref="IExtractor"/> stages for one vendor. Each stage is isolated
/// — a failure in one (a bad pattern, a regex timeout) is contained and the rest still run,
/// so a single malformed pattern can never stop ingestion.
/// </summary>
public sealed class ExtractorPipeline
{
    private readonly IReadOnlyList<IExtractor> _stages;

    public ExtractorPipeline(IReadOnlyList<IExtractor> stages) => _stages = stages;

    public static ExtractorPipeline Empty { get; } = new([]);

    public int StageCount => _stages.Count;

    public IReadOnlyList<string> StageKinds => _stages.Select(s => s.Kind).ToArray();

    public void Run(ExtractionContext context)
    {
        foreach (IExtractor stage in _stages)
        {
            try
            {
                stage.Apply(context);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                context.SetMeta("extractor_error", stage.Kind);
            }
        }

        if (context.FieldCapHit)
        {
            context.SetMeta("fields_truncated", "true");
        }
    }
}
