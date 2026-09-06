using System.Text;
using System.Text.RegularExpressions;

namespace VSoftSol.Syslog.Ingestion.Extraction;

/// <summary>
/// A small GROK dialect: <c>%{PATTERN}</c> and <c>%{PATTERN:field}</c> expand recursively
/// into a .NET regex. The base pattern set is the common Logstash one, trimmed to what the
/// core-eight vendor packs need. Every compiled regex carries a mandatory match timeout
/// (ReDoS guard, PHASE_03 Security Validation).
/// </summary>
public sealed class GrokLibrary
{
    private const int MaxExpansionDepth = 25;

    private static readonly Regex TokenPattern = new(
        @"%\{(?<name>[A-Z0-9_]+)(?::(?<field>[A-Za-z0-9_.\[\]-]+))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly IReadOnlyDictionary<string, string> BasePatterns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["INT"] = @"[+-]?[0-9]+",
        ["NONNEGINT"] = @"[0-9]+",
        ["POSINT"] = @"[1-9][0-9]*",
        ["NUMBER"] = @"[+-]?[0-9]+(?:\.[0-9]+)?",
        ["BASE16NUM"] = @"(?:0[xX])?[0-9a-fA-F]+",
        ["WORD"] = @"\w+",
        ["NOTSPACE"] = @"\S+",
        ["SPACE"] = @"\s*",
        ["DATA"] = @".*?",
        ["GREEDYDATA"] = @".*",
        ["QUOTEDSTRING"] = "(?:\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')",
        ["UUID"] = @"[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}",
        ["MAC"] = @"(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}",
        ["IPV4"] = @"(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)",
        ["IPV6"] = @"(?:[0-9A-Fa-f]{1,4}:){2,7}[0-9A-Fa-f]{0,4}(?::(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?))?",
        ["IP"] = @"(?:%{IPV6}|%{IPV4})",
        ["HOSTNAME"] = @"(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}))*)",
        ["IPORHOST"] = @"(?:%{IP}|%{HOSTNAME})",
        ["USERNAME"] = @"[A-Za-z0-9._\\@$-]+",
        ["USER"] = @"%{USERNAME}",
        ["EMAIL"] = @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        ["PATH"] = @"(?:/[^\s/]*)+|(?:[A-Za-z]:\\(?:[^\\\s]+\\?)*)",
        ["LOGLEVEL"] = @"(?i:emerg(?:ency)?|alert|crit(?:ical)?|err(?:or)?|warn(?:ing)?|notice|info(?:rmational)?|debug)",
        ["MONTH"] = @"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)",
        ["MONTHDAY"] = @"(?:[0 ]?[1-9]|[12][0-9]|3[01])",
        ["TIME"] = @"[0-2][0-9]:[0-5][0-9]:[0-5][0-9](?:\.[0-9]+)?",
        ["SYSLOGTIMESTAMP"] = @"%{MONTH}\s+%{MONTHDAY}\s%{TIME}",
        ["TIMESTAMP_ISO8601"] = @"[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:Z|[+-][0-9]{2}:?[0-9]{2})?",
        ["CISCOMNEMONIC"] = @"[A-Z0-9_]+-[0-7]-[A-Z0-9_]+",
        ["CISCOTAG"] = @"[A-Z0-9]+-[0-7]-[A-Z0-9_]+",
    };

    private readonly Dictionary<string, string> _patterns;
    private readonly TimeSpan _matchTimeout;
    private readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);

    public GrokLibrary(TimeSpan matchTimeout, IReadOnlyDictionary<string, string>? extra = null)
    {
        _matchTimeout = matchTimeout;
        _patterns = new Dictionary<string, string>(BasePatterns, StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach ((string k, string v) in extra)
            {
                _patterns[k] = v;
            }
        }
    }

    /// <summary>Compiles a GROK expression to a regex, caching by expression text.</summary>
    public Regex Compile(string grok)
    {
        if (_cache.TryGetValue(grok, out Regex? cached))
        {
            return cached;
        }

        string regex = Expand(grok, 0);
        _cache[grok] = BuildRegex(regex);
        return _cache[grok];
    }

    private Regex BuildRegex(string regex)
    {
        // Compiled for hot-path match speed; the mandatory match timeout is the ReDoS guard
        // for pack- and (Phase 5) user-authored patterns.
        return new Regex(
            regex, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Compiled, _matchTimeout);
    }

    private string Expand(string input, int depth)
    {
        if (depth > MaxExpansionDepth)
        {
            throw new GrokCompilationException("GROK pattern expansion exceeded the maximum depth (recursive pattern?).");
        }

        return TokenPattern.Replace(input, match =>
        {
            string name = match.Groups["name"].Value;
            if (!_patterns.TryGetValue(name, out string? sub))
            {
                throw new GrokCompilationException($"Unknown GROK pattern %{{{name}}}.");
            }

            string expanded = Expand(sub, depth + 1);
            string field = match.Groups["field"].Value;
            return field.Length > 0 ? $"(?<{SanitiseGroupName(field)}>{expanded})" : $"(?:{expanded})";
        });
    }

    private static string SanitiseGroupName(string field)
    {
        var sb = new StringBuilder(field.Length);
        foreach (char c in field)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        string s = sb.ToString();
        return s.Length == 0 || char.IsDigit(s[0]) ? "f_" + s : s;
    }
}

/// <summary>A GROK pattern in a pack is malformed. Surfaced at pack-load time, not ingest.</summary>
public sealed class GrokCompilationException(string message) : Exception(message);
