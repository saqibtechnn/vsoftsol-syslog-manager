using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSoftSol.Syslog.Ingestion.Extraction;

/// <summary>Matches a compiled GROK/regex against the message and stores every named group.</summary>
public sealed class GrokExtractor(Regex regex, string kind = "grok") : IExtractor
{
    public string Kind => kind;

    public void Apply(ExtractionContext context)
    {
        Match match;
        try
        {
            match = regex.Match(context.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            context.SetMeta("extractor_timeout", kind);
            return;
        }

        if (!match.Success)
        {
            return;
        }

        foreach (Group group in match.Groups)
        {
            if (group.Success && !int.TryParse(group.Name, out _))
            {
                context.Set(group.Name, group.Value);
            }
        }
    }
}

/// <summary>Parses <c>key=value</c> / <c>key="quoted value"</c> pairs (FortiGate KV format).</summary>
public sealed class KeyValueExtractor(char pairSeparator = ' ', char kvSeparator = '=', string? prefix = null) : IExtractor
{
    public string Kind => "kv";

    public void Apply(ExtractionContext context)
    {
        ReadOnlySpan<char> s = context.Message;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == pairSeparator || s[i] == ',' || s[i] == ';'))
            {
                i++;
            }

            int keyStart = i;
            while (i < s.Length && s[i] != kvSeparator && s[i] != pairSeparator)
            {
                i++;
            }

            if (i >= s.Length || s[i] != kvSeparator || i == keyStart)
            {
                // not a key=value token — skip to the next separator
                while (i < s.Length && s[i] != pairSeparator)
                {
                    i++;
                }

                continue;
            }

            string key = s[keyStart..i].ToString();
            i++; // skip '='

            string value;
            if (i < s.Length && s[i] == '"')
            {
                i++;
                int vs = i;
                while (i < s.Length && s[i] != '"')
                {
                    i++;
                }

                value = s[vs..Math.Min(i, s.Length)].ToString();
                if (i < s.Length)
                {
                    i++; // closing quote
                }
            }
            else
            {
                int vs = i;
                while (i < s.Length && s[i] != pairSeparator)
                {
                    i++;
                }

                value = s[vs..i].ToString();
            }

            context.Set(prefix is null ? key : prefix + key, value);
        }
    }
}

/// <summary>Parses the message (or a JSON object embedded in it) and flattens it to
/// <c>parent.child</c> fields. Depth-limited so deeply nested hostile input is bounded.</summary>
public sealed class JsonExtractor(int maxDepth = 12, string? prefix = null) : IExtractor
{
    public string Kind => "json";

    public void Apply(ExtractionContext context)
    {
        string msg = context.Message.Trim();
        int brace = msg.IndexOf('{', StringComparison.Ordinal);
        if (brace < 0)
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(msg[brace..], new JsonDocumentOptions { MaxDepth = maxDepth });
            Flatten(doc.RootElement, prefix ?? string.Empty, context, 0);
        }
        catch (JsonException)
        {
            // not JSON — leave the message for another stage
        }
    }

    private void Flatten(JsonElement element, string path, ExtractionContext ctx, int depth)
    {
        if (depth > maxDepth || ctx.FieldCapHit)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in element.EnumerateObject())
                {
                    Flatten(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, ctx, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                int idx = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Flatten(item, $"{path}.{idx++}", ctx, depth + 1);
                    if (idx > 100)
                    {
                        break;
                    }
                }

                break;
            case JsonValueKind.String:
                ctx.Set(path, element.GetString());
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                ctx.Set(path, element.GetRawText());
                break;
        }
    }
}

/// <summary>
/// Comma-separated positional fields with quote handling (Palo Alto PAN-OS, whose schema
/// differs per log type — several <c>[csv]</c> stages each guarded by a <c>when</c> that
/// checks a column value).
/// </summary>
public sealed class CsvExtractor(IReadOnlyList<string?> columns, int minFields = 1, int whenColumn = -1, string? whenValue = null)
    : IExtractor
{
    public string Kind => "csv";

    public void Apply(ExtractionContext context)
    {
        List<string> values = SplitCsv(context.Message);
        if (values.Count < minFields)
        {
            return;
        }

        if (whenColumn >= 0 && (whenColumn >= values.Count || !string.Equals(values[whenColumn], whenValue, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        for (int i = 0; i < values.Count && i < columns.Count; i++)
        {
            if (!string.IsNullOrEmpty(columns[i]))
            {
                context.Set(columns[i]!, values[i]);
            }
        }
    }

    internal static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }
}

/// <summary>Maps an existing field's value through a lookup table into a new field.</summary>
public sealed class LookupExtractor(string sourceField, string targetField, IReadOnlyDictionary<string, string> table) : IExtractor
{
    public string Kind => "lookup";

    public void Apply(ExtractionContext context)
    {
        if (context.TryGet(sourceField, out string value) && table.TryGetValue(value, out string? mapped))
        {
            context.Set(targetField, mapped);
        }
    }
}

/// <summary>Rename / drop / set-constant operations on the field set.</summary>
public sealed class TransformExtractor(IReadOnlyList<TransformExtractor.Op> operations) : IExtractor
{
    public string Kind => "transform";

    public enum TransformKind
    {
        Rename,
        Drop,
        Set,
    }

    public readonly record struct Op(TransformKind Kind, string A, string B);

    public void Apply(ExtractionContext context)
    {
        foreach (Op op in operations)
        {
            switch (op.Kind)
            {
                case TransformKind.Rename:
                    context.Rename(op.A, op.B);
                    break;
                case TransformKind.Drop:
                    context.Remove(op.A);
                    break;
                case TransformKind.Set:
                    context.Set(op.A, op.B);
                    break;
            }
        }
    }
}
