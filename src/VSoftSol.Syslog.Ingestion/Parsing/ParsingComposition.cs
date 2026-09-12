using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Patterns;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// Builds a ready-to-use <see cref="MessageParser"/> + <see cref="DeduplicationWindow"/>
/// outside DI — for the out-of-process probe, the benchmark, and tests. The normal path is
/// <c>AddSyslogIngestion</c>.
/// </summary>
public static class ParsingComposition
{
    public static (MessageParser Parser, DeduplicationWindow Dedup) Build(
        ParsingOptions? options = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null,
        UserExtractorRegistry? userExtractors = null)
    {
        options ??= new ParsingOptions();
        IOptions<ParsingOptions> wrapped = Options.Create(options);
        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;

        var loader = new PatternPackLoader(wrapped, factory.CreateLogger<PatternPackLoader>());
        var vendor = new VendorExtractor(loader, wrapped, userExtractors ?? new UserExtractorRegistry());
        var parser = new MessageParser(new Rfc5424Parser(), new Rfc3164Parser(), vendor, wrapped);
        var dedup = new DeduplicationWindow(wrapped, timeProvider);
        return (parser, dedup);
    }
}
