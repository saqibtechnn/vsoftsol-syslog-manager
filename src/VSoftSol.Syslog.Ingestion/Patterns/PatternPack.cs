using System.Text.RegularExpressions;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Ingestion.Patterns;

/// <summary>
/// A vendor parser pack, loaded at runtime from a plain-text <c>.pack</c> file
/// (PHASE_03 item 7 — "loaded at runtime, not compiled in"). Holds the recognition rules
/// and the ordered extractor pipeline for one vendor.
/// </summary>
public sealed class PatternPack
{
    public required string Vendor { get; init; }

    public int Priority { get; init; } = 100;

    /// <summary>The source file, for diagnostics.</summary>
    public required string SourcePath { get; init; }

    public required IReadOnlyList<MatchRule> MatchRules { get; init; }

    public required ExtractorPipeline Pipeline { get; init; }

    /// <summary>True when <paramref name="parse"/> looks like this vendor's output.</summary>
    public bool Matches(SyslogParseResult parse, string sourceIp)
    {
        foreach (MatchRule rule in MatchRules)
        {
            string? subject = rule.Field switch
            {
                MatchField.AppName => parse.AppName,
                MatchField.Hostname => parse.Hostname,
                MatchField.Message => parse.Message,
                MatchField.MsgId => parse.MsgId,
                MatchField.ProcId => parse.ProcId,
                MatchField.SourceIp => sourceIp,
                _ => null,
            };

            if (subject is null)
            {
                continue;
            }

            try
            {
                if (rule.Pattern.IsMatch(subject))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // a hostile message cannot force a match by timing out
            }
        }

        return false;
    }

    public enum MatchField
    {
        AppName,
        Hostname,
        Message,
        MsgId,
        ProcId,
        SourceIp,
    }

    public readonly record struct MatchRule(MatchField Field, Regex Pattern);
}
