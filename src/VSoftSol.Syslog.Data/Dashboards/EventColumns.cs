using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// The canonical field-token → <c>events</c> column map for aggregation SQL. Kept as an
/// allow-list so a group-by / value field can never be interpolated from user input
/// (SECURITY_STANDARDS §5.1 / §5.3). Mirrors the groupable set in
/// <see cref="VSoftSol.Syslog.Data.Alerts.SqliteAlertWindowReader"/> — a unit test asserts
/// the two never drift.
/// </summary>
internal static class EventColumns
{
    /// <summary>Field token → column name, for group-by and string value fields.</summary>
    public static readonly IReadOnlyDictionary<string, string> Groupable = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hostname"] = "hostname",
        ["source_ip"] = "source_ip",
        ["app"] = "app_name",
        ["proc_id"] = "proc_id",
        ["msg_id"] = "msg_id",
        ["severity"] = "severity",
        ["facility"] = "facility",
        ["vendor"] = "vendor",
        ["protocol"] = "protocol",
        ["parse_status"] = "parse_status",
    };

    /// <summary>Field token → column name, for the arithmetic functions (sum / avg / min / max).</summary>
    public static readonly IReadOnlyDictionary<string, string> Numeric = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["severity"] = "severity",
        ["facility"] = "facility",
        ["occurrence_count"] = "occurrence_count",
    };

    /// <summary>The <c>field.&lt;name&gt;</c> prefix.</summary>
    public const string ExtractedPrefix = ConditionFields.ExtractedFieldPrefix;

    /// <summary>
    /// Resolves a group-by token to <c>(column, extractedName)</c>: a built-in column, or an
    /// extracted-field name to be looked up in <c>event_fields</c>. Returns false for
    /// anything not on the allow-list.
    /// </summary>
    public static bool TryResolveGroup(string token, out string? column, out string? extractedName)
    {
        column = null;
        extractedName = null;
        if (Groupable.TryGetValue(token, out string? c))
        {
            column = c;
            return true;
        }

        if (TryExtractedName(token, out string? name))
        {
            extractedName = name;
            return true;
        }

        return false;
    }

    /// <summary>As <see cref="TryResolveGroup"/> but for a numeric value field.</summary>
    public static bool TryResolveNumeric(string token, out string? column, out string? extractedName)
    {
        column = null;
        extractedName = null;
        if (Numeric.TryGetValue(token, out string? c))
        {
            column = c;
            return true;
        }

        if (TryExtractedName(token, out string? name))
        {
            extractedName = name;
            return true;
        }

        return false;
    }

    private static bool TryExtractedName(string token, out string? name)
    {
        name = null;
        if (!token.StartsWith(ExtractedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reuse the Core validity check (1-64 chars, letters / digits / _ - .).
        if (!ConditionFields.TryResolve(token, out ConditionFieldInfo? info) || info.Type != ConditionFieldType.ExtractedField)
        {
            return false;
        }

        name = token[ExtractedPrefix.Length..];
        return true;
    }
}
