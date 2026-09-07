using System.Diagnostics.CodeAnalysis;

namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// The kind of value a <see cref="SearchField"/> accepts. Drives literal validation in the
/// parser and predicate shape in the SQL compiler.
/// </summary>
public enum SearchValueType
{
    /// <summary>Free-text, full-text indexed (message body, raw text).</summary>
    FreeText,

    /// <summary>An exact / prefix string column (hostname, app name, vendor, …).</summary>
    Text,

    /// <summary>A dotted IPv4/IPv6 literal (exact or prefix).</summary>
    IpAddress,

    /// <summary>Syslog severity — a name (<c>error</c>) or a code 0-7.</summary>
    Severity,

    /// <summary>Syslog facility — a name (<c>local0</c>) or a code 0-23.</summary>
    Facility,

    /// <summary>A whole number column (event_id, device_id).</summary>
    Number,

    /// <summary>A UTC timestamp column (received_utc, event_utc).</summary>
    Timestamp,

    /// <summary>Transport protocol — <c>udp</c> / <c>tcp</c> / <c>tls</c> / <c>snmp</c> / <c>wineventlog</c>.</summary>
    Protocol,

    /// <summary>Parse status — <c>raw</c> / <c>rfc3164</c> / <c>rfc5424</c>.</summary>
    ParseStatus,

    /// <summary>A named reference resolved against another table (device name, stream name).</summary>
    Reference,

    /// <summary>A user-extracted field (<c>event_fields</c>); addressed as <c>field.&lt;name&gt;</c>.</summary>
    CustomField,
}

/// <summary>
/// One addressable field in the query language. Fields are an allow-list
/// (SECURITY_STANDARDS.md — allow-lists not deny-lists): a token that does not resolve to
/// a registered field, or to a <c>field.&lt;name&gt;</c> custom field, is a parse error, not
/// a silent no-op.
/// </summary>
public sealed record SearchField
{
    /// <summary>The canonical name used in diagnostics and by the SQL compiler.</summary>
    public required string CanonicalName { get; init; }

    /// <summary>Alternate spellings a user may type. Case-insensitive.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The literal type this field accepts.</summary>
    public required SearchValueType ValueType { get; init; }

    /// <summary>
    /// True when <c>&gt; &gt;= &lt; &lt;=</c> are allowed. Text and free-text fields only
    /// support <c>: (equals)</c> and <c>!= (not equals)</c>.
    /// </summary>
    public bool SupportsRangeOperators { get; init; }

    /// <summary>
    /// For <see cref="SearchValueType.CustomField"/> — the <c>event_fields.name</c> this
    /// term filters on. Null for every built-in field.
    /// </summary>
    public string? CustomFieldName { get; init; }
}

/// <summary>
/// The registry of built-in query fields plus resolution of dynamic
/// <c>field.&lt;name&gt;</c> custom fields.
/// </summary>
public static class SearchFields
{
    /// <summary>The prefix that addresses a user-extracted field, e.g. <c>field.srcport</c>.</summary>
    public const string CustomFieldPrefix = "field.";

    private static readonly SearchField[] BuiltInFields =
    [
        new() { CanonicalName = "message", Aliases = ["msg", "text"], ValueType = SearchValueType.FreeText },
        new() { CanonicalName = "raw", Aliases = ["raw_message"], ValueType = SearchValueType.FreeText },
        new() { CanonicalName = "host", Aliases = ["hostname"], ValueType = SearchValueType.Text },
        new() { CanonicalName = "source_ip", Aliases = ["ip", "src", "src_ip", "sourceip"], ValueType = SearchValueType.IpAddress },
        new() { CanonicalName = "app", Aliases = ["app_name", "appname", "program", "tag"], ValueType = SearchValueType.Text },
        new() { CanonicalName = "proc_id", Aliases = ["pid", "procid"], ValueType = SearchValueType.Text },
        new() { CanonicalName = "msg_id", Aliases = ["msgid"], ValueType = SearchValueType.Text },
        new() { CanonicalName = "severity", Aliases = ["sev", "level"], ValueType = SearchValueType.Severity, SupportsRangeOperators = true },
        new() { CanonicalName = "facility", Aliases = ["fac"], ValueType = SearchValueType.Facility, SupportsRangeOperators = true },
        new() { CanonicalName = "vendor", Aliases = [], ValueType = SearchValueType.Text },
        new() { CanonicalName = "protocol", Aliases = ["proto"], ValueType = SearchValueType.Protocol },
        new() { CanonicalName = "parse_status", Aliases = ["parsed", "status"], ValueType = SearchValueType.ParseStatus },
        new() { CanonicalName = "device", Aliases = ["device_name"], ValueType = SearchValueType.Reference },
        new() { CanonicalName = "device_id", Aliases = [], ValueType = SearchValueType.Number, SupportsRangeOperators = true },
        new() { CanonicalName = "stream", Aliases = ["stream_name"], ValueType = SearchValueType.Reference },
        new() { CanonicalName = "event_id", Aliases = ["id", "event"], ValueType = SearchValueType.Number, SupportsRangeOperators = true },
        new() { CanonicalName = "received", Aliases = ["received_utc", "time", "received_time"], ValueType = SearchValueType.Timestamp, SupportsRangeOperators = true },
        new() { CanonicalName = "event_time", Aliases = ["event_utc", "timestamp"], ValueType = SearchValueType.Timestamp, SupportsRangeOperators = true },
    ];

    private static readonly Dictionary<string, SearchField> ByName =
        BuildIndex();

    /// <summary>Every built-in field, in registry order. Backs autocomplete.</summary>
    public static IReadOnlyList<SearchField> All => BuiltInFields;

    /// <summary>The free-text field used when a term has no <c>field:</c> prefix.</summary>
    public static SearchField FreeText => ByName["message"];

    /// <summary>
    /// Resolves a field token. Accepts a canonical name, an alias (case-insensitive), or
    /// <c>field.&lt;name&gt;</c> for a custom field. The custom-field name is validated:
    /// 1-64 chars, letters / digits / <c>_ - .</c> only.
    /// </summary>
    public static bool TryResolve(string token, [NotNullWhen(true)] out SearchField? field)
    {
        field = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.StartsWith(CustomFieldPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = token[CustomFieldPrefix.Length..];
            if (!IsValidCustomFieldName(name))
            {
                return false;
            }

            field = new SearchField
            {
                CanonicalName = CustomFieldPrefix + name,
                ValueType = SearchValueType.CustomField,
                SupportsRangeOperators = true,
                CustomFieldName = name,
            };
            return true;
        }

        return ByName.TryGetValue(token, out field);
    }

    private static bool IsValidCustomFieldName(string name)
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

    private static Dictionary<string, SearchField> BuildIndex()
    {
        var index = new Dictionary<string, SearchField>(StringComparer.OrdinalIgnoreCase);
        foreach (SearchField f in BuiltInFields)
        {
            index[f.CanonicalName] = f;
            foreach (string alias in f.Aliases)
            {
                index[alias] = f;
            }
        }

        return index;
    }
}
