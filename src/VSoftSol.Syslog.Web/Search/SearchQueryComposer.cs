using System.Text;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Web.Search;

/// <summary>
/// Turns the filter sidebar's selections into query-language text and shows the result in
/// the query bar — so a network admin who never types a query still learns the syntax by
/// seeing what the sidebar produced (UX_STANDARDS §3; PHASE_05 item 9: "Typing a query
/// must never be required").
/// </summary>
public static class SearchQueryComposer
{
    /// <summary>
    /// Composes the effective query from the free-text the user typed plus the sidebar
    /// selections. Sidebar terms are ANDed after the free text; multi-select dimensions
    /// become a parenthesised OR group.
    /// </summary>
    public static string Compose(
        string? freeText,
        IReadOnlyCollection<Severity> severities,
        IReadOnlyCollection<string> deviceNames,
        IReadOnlyCollection<string> streamNames)
    {
        var parts = new List<string>();

        string trimmed = (freeText ?? string.Empty).Trim();
        if (trimmed.Length > 0)
        {
            parts.Add(trimmed);
        }

        if (severities.Count > 0)
        {
            parts.Add(OrGroup(severities.Select(s => $"severity:{s.ToString().ToLowerInvariant()}")));
        }

        if (deviceNames.Count > 0)
        {
            parts.Add(OrGroup(deviceNames.Select(d => $"device:{Quote(d)}")));
        }

        if (streamNames.Count > 0)
        {
            parts.Add(OrGroup(streamNames.Select(s => $"stream:{Quote(s)}")));
        }

        return string.Join(' ', parts);
    }

    private static string OrGroup(IEnumerable<string> terms)
    {
        string[] list = terms.ToArray();
        return list.Length == 1 ? list[0] : "(" + string.Join(" OR ", list) + ")";
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    /// <summary>
    /// Autocomplete suggestions for the token currently being typed. Returns field names
    /// when the caret is on a bare word, and known values when it is just after
    /// <c>field:</c>.
    /// </summary>
    public static IReadOnlyList<string> Suggest(string queryUpToCaret, IReadOnlyCollection<string> knownFieldValues)
    {
        string token = LastToken(queryUpToCaret);

        int colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            string field = token[..colon];
            string partial = token[(colon + 1)..];
            return field.Equals("severity", StringComparison.OrdinalIgnoreCase)
                ? SeverityNames.Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).ToArray()
                : knownFieldValues
                    .Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    .Take(10).ToArray();
        }

        return Core.Search.SearchFields.All
            .Select(f => f.CanonicalName + ":")
            .Where(n => n.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Concat(Keywords.Where(k => k.StartsWith(token, StringComparison.OrdinalIgnoreCase)))
            .Take(12)
            .ToArray();
    }

    private static readonly string[] SeverityNames =
        ["emergency", "alert", "critical", "error", "warning", "notice", "info", "debug"];

    private static readonly string[] Keywords = ["AND", "OR", "NOT"];

    private static string LastToken(string text)
    {
        var sb = new StringBuilder();
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c is '(' or ')')
            {
                break;
            }

            sb.Insert(0, c);
        }

        return sb.ToString();
    }
}
