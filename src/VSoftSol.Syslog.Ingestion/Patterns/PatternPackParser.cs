using System.Globalization;
using System.Text.RegularExpressions;
using VSoftSol.Syslog.Ingestion.Extraction;

namespace VSoftSol.Syslog.Ingestion.Patterns;

/// <summary>
/// Parses a <c>.pack</c> text file into a <see cref="PatternPack"/>. Format is INI-like:
/// <c>[section]</c> headers (repeatable — <c>[grok]</c>/<c>[regex]</c> stages run in file
/// order), <c>#</c> comments, <c>key = value</c> lines. A malformed pack throws
/// <see cref="PatternPackException"/> at load time, never at ingest.
/// </summary>
public sealed class PatternPackParser
{
    private readonly GrokLibrary _grok;
    private readonly TimeSpan _regexTimeout;

    public PatternPackParser(GrokLibrary grok, TimeSpan regexTimeout)
    {
        _grok = grok;
        _regexTimeout = regexTimeout;
    }

    public PatternPack Parse(string path, string content)
    {
        var lines = content.Split('\n');
        string vendor = Path.GetFileNameWithoutExtension(path);
        int priority = 100;
        var matchRules = new List<PatternPack.MatchRule>();
        var stages = new List<IExtractor>();

        string section = string.Empty;
        var sectionLines = new List<string>();

        void FlushSection()
        {
            switch (section)
            {
                case "":
                case "pack":
                    foreach (string l in sectionLines)
                    {
                        (string k, string v) = SplitKv(l);
                        if (k == "priority" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                        {
                            priority = p;
                        }
                        else if (k == "vendor" && v.Length > 0)
                        {
                            vendor = v;
                        }
                    }

                    break;

                case "match":
                    foreach (string l in sectionLines)
                    {
                        matchRules.Add(ParseMatchRule(l, path));
                    }

                    break;

                case "grok":
                case "regex":
                    string expr = string.Join(string.Empty, sectionLines).Trim();
                    if (expr.Length > 0)
                    {
                        stages.Add(new GrokExtractor(BuildRegex(expr, section, path), section));
                    }

                    break;

                case "kv":
                    stages.Add(ParseKv(sectionLines));
                    break;

                case "json":
                    stages.Add(new JsonExtractor());
                    break;

                case "csv":
                    stages.Add(ParseCsv(sectionLines, path));
                    break;

                case "lookup":
                    stages.Add(ParseLookup(sectionLines, path));
                    break;

                case "transform":
                    stages.Add(ParseTransform(sectionLines));
                    break;

                default:
                    throw new PatternPackException($"{path}: unknown section [{section}].");
            }

            sectionLines.Clear();
        }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                FlushSection();
                section = trimmed[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            sectionLines.Add(line);
        }

        FlushSection();

        return new PatternPack
        {
            Vendor = vendor,
            Priority = priority,
            SourcePath = path,
            MatchRules = matchRules,
            Pipeline = new ExtractorPipeline(stages),
        };
    }

    private Regex BuildRegex(string expr, string kind, string path)
    {
        // [grok] and [regex] both accept %{PATTERN:field} tokens; [regex] just reads as
        // plain regex when it has none. GROK expansion carries the mandatory match timeout.
        try
        {
            return _grok.Compile(expr);
        }
        catch (Exception ex) when (ex is GrokCompilationException or ArgumentException)
        {
            throw new PatternPackException($"{path}: bad {kind} pattern: {ex.Message}", ex);
        }
    }

    private PatternPack.MatchRule ParseMatchRule(string line, string path)
    {
        int tilde = line.IndexOf('~', StringComparison.Ordinal);
        if (tilde < 0)
        {
            throw new PatternPackException($"{path}: match rule must be '<field> ~ <regex>': {line}");
        }

        string field = line[..tilde].Trim().ToLowerInvariant();
        string pattern = line[(tilde + 1)..].Trim();
        PatternPack.MatchField mf = field switch
        {
            "appname" or "app_name" or "tag" => PatternPack.MatchField.AppName,
            "hostname" or "host" => PatternPack.MatchField.Hostname,
            "message" or "msg" => PatternPack.MatchField.Message,
            "msgid" or "msg_id" => PatternPack.MatchField.MsgId,
            "procid" or "proc_id" => PatternPack.MatchField.ProcId,
            "sourceip" or "source_ip" => PatternPack.MatchField.SourceIp,
            _ => throw new PatternPackException($"{path}: unknown match field '{field}'."),
        };

        try
        {
            var rx = new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
                _regexTimeout);
            return new PatternPack.MatchRule(mf, rx);
        }
        catch (ArgumentException ex)
        {
            throw new PatternPackException($"{path}: bad match regex '{pattern}': {ex.Message}", ex);
        }
    }

    private static KeyValueExtractor ParseKv(List<string> lines)
    {
        char pairSep = ' ';
        char kvSep = '=';
        string? prefix = null;
        foreach (string l in lines)
        {
            (string k, string v) = SplitKv(l);
            switch (k)
            {
                case "pairsep" when v.Length > 0:
                    pairSep = v == "\\t" ? '\t' : v[0];
                    break;
                case "kvsep" when v.Length > 0:
                    kvSep = v[0];
                    break;
                case "prefix":
                    prefix = v;
                    break;
            }
        }

        return new KeyValueExtractor(pairSep, kvSep, prefix);
    }

    private static CsvExtractor ParseCsv(List<string> lines, string path)
    {
        IReadOnlyList<string?> columns = [];
        int minFields = 1;
        int whenColumn = -1;
        string? whenValue = null;
        foreach (string l in lines)
        {
            (string k, string v) = SplitKv(l);
            switch (k)
            {
                case "columns":
                    columns = v.Split(',').Select(c => c.Trim() is { Length: > 0 } t ? t : null).ToArray();
                    break;
                case "minfields" when int.TryParse(v, out int mf):
                    minFields = mf;
                    break;
                case "when":
                    // "when = <columnIndex>=<value>"
                    int eq = v.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0 && int.TryParse(v[..eq].Trim(), out int wc))
                    {
                        whenColumn = wc;
                        whenValue = v[(eq + 1)..].Trim();
                    }

                    break;
            }
        }

        if (columns.Count == 0)
        {
            throw new PatternPackException($"{path}: [csv] needs a 'columns = ...' line.");
        }

        return new CsvExtractor(columns, minFields, whenColumn, whenValue);
    }

    private static LookupExtractor ParseLookup(List<string> lines, string path)
    {
        string? source = null;
        string? target = null;
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string l in lines)
        {
            (string k, string v) = SplitKv(l);
            if (k == "source")
            {
                source = v;
            }
            else if (k == "target")
            {
                target = v;
            }
            else if (k.Length > 0)
            {
                table[k] = v;
            }
        }

        if (source is null || target is null)
        {
            throw new PatternPackException($"{path}: [lookup] needs 'source' and 'target' lines.");
        }

        return new LookupExtractor(source, target, table);
    }

    private static TransformExtractor ParseTransform(List<string> lines)
    {
        var ops = new List<TransformExtractor.Op>();
        foreach (string l in lines)
        {
            string[] parts = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "rename" when parts.Length >= 3:
                    ops.Add(new TransformExtractor.Op(TransformExtractor.TransformKind.Rename, parts[1], parts[2]));
                    break;
                case "drop" when parts.Length >= 2:
                    ops.Add(new TransformExtractor.Op(TransformExtractor.TransformKind.Drop, parts[1], string.Empty));
                    break;
                case "set" when parts.Length >= 3:
                    ops.Add(new TransformExtractor.Op(TransformExtractor.TransformKind.Set, parts[1], string.Join(' ', parts[2..])));
                    break;
            }
        }

        return new TransformExtractor(ops);
    }

    private static (string Key, string Value) SplitKv(string line)
    {
        int eq = line.IndexOf('=', StringComparison.Ordinal);
        return eq < 0
            ? (line.Trim().ToLowerInvariant(), string.Empty)
            : (line[..eq].Trim().ToLowerInvariant(), line[(eq + 1)..].Trim());
    }
}

/// <summary>A vendor <c>.pack</c> file is malformed. Thrown at load time.</summary>
public sealed class PatternPackException : Exception
{
    public PatternPackException(string message) : base(message)
    {
    }

    public PatternPackException(string message, Exception inner) : base(message, inner)
    {
    }
}
