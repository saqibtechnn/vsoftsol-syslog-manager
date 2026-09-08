using System.Diagnostics.CodeAnalysis;

namespace VSoftSol.Syslog.Core.Conditions;

/// <summary>What kind of value a condition field holds — drives operator validation.</summary>
public enum ConditionFieldType
{
    /// <summary>Free text (message body, hostname, app name, …).</summary>
    Text,

    /// <summary>Syslog severity — a name or a code 0-7.</summary>
    Severity,

    /// <summary>Syslog facility — a name or a code 0-23.</summary>
    Facility,

    /// <summary>A whole number (occurrence count).</summary>
    Number,

    /// <summary>A short enumerated token (protocol, parse status).</summary>
    Token,

    /// <summary>A user-extracted <c>event_fields</c> value, addressed as <c>field.&lt;name&gt;</c>.</summary>
    ExtractedField,
}

/// <summary>One field a stream / rule / alert condition can test.</summary>
public sealed record ConditionFieldInfo(string Name, string Label, ConditionFieldType Type);

/// <summary>
/// The allow-list of fields a <see cref="ConditionComparison"/> may reference. Shared by
/// the ingest-path evaluator and the visual builder so both agree on what is testable.
/// </summary>
public static class ConditionFields
{
    /// <summary>The prefix that addresses an extracted field, e.g. <c>field.srcport</c>.</summary>
    public const string ExtractedFieldPrefix = "field.";

    /// <summary>Built-in fields, in the order the builder should list them.</summary>
    public static IReadOnlyList<ConditionFieldInfo> BuiltIn { get; } =
    [
        new("message", "Message text", ConditionFieldType.Text),
        new("hostname", "Hostname", ConditionFieldType.Text),
        new("source_ip", "Source IP", ConditionFieldType.Text),
        new("app", "Application / tag", ConditionFieldType.Text),
        new("proc_id", "Process ID", ConditionFieldType.Text),
        new("msg_id", "Message ID", ConditionFieldType.Text),
        new("severity", "Severity", ConditionFieldType.Severity),
        new("facility", "Facility", ConditionFieldType.Facility),
        new("vendor", "Vendor", ConditionFieldType.Text),
        new("protocol", "Protocol", ConditionFieldType.Token),
        new("parse_status", "Parse status", ConditionFieldType.Token),
        new("occurrence_count", "Occurrence count", ConditionFieldType.Number),
    ];

    private static readonly Dictionary<string, ConditionFieldInfo> ByName =
        BuiltIn.SelectMany(f => Aliases(f).Select(a => (a, f)))
            .ToDictionary(x => x.a, x => x.f, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Aliases(ConditionFieldInfo f) => f.Name switch
    {
        "hostname" => ["hostname", "host"],
        "source_ip" => ["source_ip", "ip", "src_ip"],
        "app" => ["app", "app_name", "program", "tag"],
        "occurrence_count" => ["occurrence_count", "occurrences"],
        _ => [f.Name],
    };

    /// <summary>
    /// Resolves a field token to its metadata. Accepts a built-in name/alias
    /// (case-insensitive) or <c>field.&lt;name&gt;</c> (name: 1-64 chars, letters / digits /
    /// <c>_ - .</c>).
    /// </summary>
    public static bool TryResolve(string? token, [NotNullWhen(true)] out ConditionFieldInfo? field)
    {
        field = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.StartsWith(ExtractedFieldPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = token[ExtractedFieldPrefix.Length..];
            if (!IsValidExtractedName(name))
            {
                return false;
            }

            field = new ConditionFieldInfo(ExtractedFieldPrefix + name, "field: " + name, ConditionFieldType.ExtractedField);
            return true;
        }

        return ByName.TryGetValue(token, out field);
    }

    private static bool IsValidExtractedName(string name)
    {
        if (name.Length is 0 or > 64)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
