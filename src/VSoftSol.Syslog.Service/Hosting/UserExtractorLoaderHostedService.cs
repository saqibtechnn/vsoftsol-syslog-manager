using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// v1.1 — P5-3 (`docs/evidence/phase-05/known-issues.md`): loads every enabled row from
/// <c>user_extractors</c> (saved from the Settings → Pattern tester), compiles each with the
/// same <see cref="GrokLibrary"/>/match-timeout ReDoS guard vendor packs already use, and
/// publishes the result to <see cref="UserExtractorRegistry"/> for <c>VendorExtractor</c> to
/// run on every message. A malformed saved pattern (the store itself does not validate regex
/// syntax) is logged and skipped — same contract as a malformed <c>.pack</c> file; it never
/// stops the collector. Same disposition as vendor packs: only needs the database (already
/// migrated by <see cref="DatabaseInitializer"/>), not the listener sockets, so it does not
/// need to run after <c>IngestionHostedService</c>.
/// </summary>
public sealed class UserExtractorLoaderHostedService : IHostedService
{
    private readonly SqliteExtractorStore _store;
    private readonly UserExtractorRegistry _registry;
    private readonly ParsingOptions _parsing;
    private readonly ILogger<UserExtractorLoaderHostedService> _logger;

    public UserExtractorLoaderHostedService(
        SqliteExtractorStore store,
        UserExtractorRegistry registry,
        IOptions<ParsingOptions> parsing,
        ILogger<UserExtractorLoaderHostedService> logger)
    {
        _store = store;
        _registry = registry;
        _parsing = parsing.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UserExtractor> saved = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var grok = new GrokLibrary(_parsing.RegexTimeout);
        var stages = new List<IExtractor>();
        int skipped = 0;

        foreach (UserExtractor extractor in saved)
        {
            if (!extractor.Enabled)
            {
                continue;
            }

            try
            {
                stages.Add(new GrokExtractor(grok.Compile(extractor.Pattern), extractor.Kind));
            }
            catch (Exception ex) when (ex is GrokCompilationException or ArgumentException)
            {
                skipped++;
                _logger.LogError(ex, "Skipping malformed saved extractor {Name}.", extractor.Name);
            }
        }

        _registry.SetPipeline(new ExtractorPipeline(stages));
        _logger.LogInformation(
            "Loaded {Count} user-authored extractor(s) ({Skipped} skipped as malformed).", stages.Count, skipped);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
