using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Ingestion.Patterns;

/// <summary>
/// Resolves the vendor for a parsed message and runs that vendor's extractor pipeline.
/// Packs are loaded once at construction; the resolver is immutable and thread-safe.
/// </summary>
public sealed class VendorExtractor
{
    private readonly IReadOnlyList<PatternPack> _packs;
    private readonly int _maxFields;
    private readonly int _maxValueLength;

    public VendorExtractor(PatternPackLoader loader, IOptions<ParsingOptions> options)
        : this(loader.Load(), options.Value)
    {
    }

    public VendorExtractor(IReadOnlyList<PatternPack> packs, ParsingOptions options)
    {
        _packs = packs;
        _maxFields = options.MaxFieldsPerMessage;
        _maxValueLength = options.MaxFieldValueLength;
    }

    public IReadOnlyList<string> LoadedVendors => _packs.Select(p => p.Vendor).ToArray();

    public readonly record struct Result(string? Vendor, IReadOnlyList<EventField> Fields);

    /// <summary>Finds the first matching pack (lowest priority number wins) and extracts.</summary>
    public Result Enrich(SyslogParseResult parse, string sourceIp)
    {
        foreach (PatternPack pack in _packs)
        {
            if (!pack.Matches(parse, sourceIp))
            {
                continue;
            }

            var ctx = new ExtractionContext(parse.Message, sourceIp, parse.Hostname, parse.AppName, _maxFields, _maxValueLength);
            pack.Pipeline.Run(ctx);

            var fields = new List<EventField>(ctx.Fields.Count);
            foreach ((string name, string value) in ctx.Fields)
            {
                fields.Add(new EventField(name, value));
            }

            return new Result(pack.Vendor, fields);
        }

        return new Result(null, []);
    }
}
