using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Ingestion.Patterns;

/// <summary>
/// Discovers and loads vendor parser packs from <c>&lt;PatternsDirectory&gt;/&lt;vendor&gt;/*.pack</c>
/// at runtime. A malformed pack is logged and skipped — it never stops the collector, and
/// dropping in a new pack needs only a restart, no rebuild (PHASE_03 item 7).
/// </summary>
public sealed class PatternPackLoader
{
    private readonly ParsingOptions _options;
    private readonly ILogger<PatternPackLoader> _logger;

    public PatternPackLoader(IOptions<ParsingOptions> options, ILogger<PatternPackLoader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string ResolveDirectory() =>
        string.IsNullOrWhiteSpace(_options.PatternsDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "Patterns")
            : _options.PatternsDirectory;

    public IReadOnlyList<PatternPack> Load()
    {
        string dir = ResolveDirectory();
        var grok = new GrokLibrary(_options.RegexTimeout);
        var parser = new PatternPackParser(grok, _options.RegexTimeout);
        var packs = new List<PatternPack>();

        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("Pattern directory {Directory} does not exist; no vendor packs loaded.", dir);
            return packs;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.pack", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                packs.Add(parser.Parse(file, File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is PatternPackException or IOException)
            {
                _logger.LogError(ex, "Skipping malformed vendor pack {File}.", file);
            }
        }

        packs.Sort((a, b) => a.Priority != b.Priority
            ? a.Priority.CompareTo(b.Priority)
            : string.CompareOrdinal(a.Vendor, b.Vendor));

        _logger.LogInformation(
            "Loaded {Count} vendor parser pack(s) from {Directory}: {Vendors}.",
            packs.Count, dir, string.Join(", ", packs.Select(p => p.Vendor)));
        return packs;
    }
}
