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
    private readonly UserExtractorRegistry _userExtractors;

    public VendorExtractor(PatternPackLoader loader, IOptions<ParsingOptions> options)
        : this(loader.Load(), options.Value)
    {
    }

    public VendorExtractor(PatternPackLoader loader, IOptions<ParsingOptions> options, UserExtractorRegistry userExtractors)
        : this(loader.Load(), options.Value, userExtractors)
    {
    }

    public VendorExtractor(IReadOnlyList<PatternPack> packs, ParsingOptions options, UserExtractorRegistry? userExtractors = null)
    {
        _packs = packs;
        _maxFields = options.MaxFieldsPerMessage;
        _maxValueLength = options.MaxFieldValueLength;
        _userExtractors = userExtractors ?? new UserExtractorRegistry();
    }

    public IReadOnlyList<string> LoadedVendors => _packs.Select(p => p.Vendor).ToArray();

    public readonly record struct Result(string? Vendor, IReadOnlyList<EventField> Fields);

    /// <summary>
    /// Finds the first matching pack (lowest priority number wins), then always runs the
    /// operator-saved global extractors (P5-3) on the same context — including when no
    /// vendor matched at all, since those are exactly the user-extensible path for a device
    /// with no built-in pack. A user extractor field name that collides with a pack field
    /// overwrites it (it ran later), the same last-writer-wins rule every multi-stage
    /// pipeline already applies.
    /// </summary>
    public Result Enrich(SyslogParseResult parse, string sourceIp)
    {
        PatternPack? matched = null;
        foreach (PatternPack pack in _packs)
        {
            if (pack.Matches(parse, sourceIp))
            {
                matched = pack;
                break;
            }
        }

        ExtractorPipeline userPipeline = _userExtractors.Current;
        if (matched is null && userPipeline.StageCount == 0)
        {
            return new Result(null, []);
        }

        var ctx = new ExtractionContext(parse.Message, sourceIp, parse.Hostname, parse.AppName, _maxFields, _maxValueLength);
        matched?.Pipeline.Run(ctx);
        userPipeline.Run(ctx);

        var fields = new List<EventField>(ctx.Fields.Count);
        foreach ((string name, string value) in ctx.Fields)
        {
            fields.Add(new EventField(name, value));
        }

        return new Result(matched?.Vendor, fields);
    }
}
