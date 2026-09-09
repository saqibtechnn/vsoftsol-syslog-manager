using System.Text;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Rules.Conditions;

namespace VSoftSol.Syslog.Rules.Templating;

/// <summary>
/// Literal <c>{field}</c> / <c>{field.name}</c> substitution over an event's own projection
/// (PHASE_07 — templated subjects, bodies, headers, script args, file lines). This is
/// <b>not</b> an expression language: a token is a single field lookup against
/// <see cref="ConditionFields"/> (reusing <see cref="EventFieldReader"/>); there are no
/// method calls, no arithmetic, and no path traversal beyond the known field set
/// (template-injection defence, SECURITY_STANDARDS.md). An unknown token renders empty.
/// A literal brace is written <c>{{</c> or <c>}}</c>.
/// </summary>
public static class FieldTemplate
{
    private const int MaxOutputChars = 64 * 1024;

    /// <summary>Render-only tokens beyond the condition field set (timestamps, ids).</summary>
    private static readonly HashSet<string> ExtraTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "received", "received_utc", "event_time", "event_id", "device_id", "stream_count",
    };

    private static bool IsKnown(string token) =>
        ExtraTokens.Contains(token) || ConditionFields.TryResolve(token, out _);

    /// <summary>
    /// Validates that every <c>{token}</c> in <paramref name="template"/> names a known
    /// field. Returns false and fills <paramref name="error"/> on an unbalanced brace or an
    /// unknown field.
    /// </summary>
    public static bool TryValidate(string? template, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(template))
        {
            return true;
        }

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    i++;
                    continue;
                }

                error = $"Unbalanced '}}' at position {i}.";
                return false;
            }

            if (c != '{')
            {
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                i++;
                continue;
            }

            int end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                error = $"Unclosed '{{' at position {i}.";
                return false;
            }

            string token = template[(i + 1)..end].Trim();
            if (token.Length == 0)
            {
                // `{}` is common in JSON bodies — treat an empty pair as a literal, not a token.
                i = end;
                continue;
            }

            if (!IsKnown(token))
            {
                error = $"Unknown field '{token}'.";
                return false;
            }

            i = end;
        }

        return true;
    }

    /// <summary>
    /// Renders <paramref name="template"/> against <paramref name="e"/>. Output is capped at
    /// 64&#160;KB. Control characters other than tab are dropped from substituted values
    /// (CR/LF here would enable SMTP-header / log-forging injection downstream).
    /// </summary>
    public static string Render(string? template, SyslogEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(template.Length + 64);
        for (int i = 0; i < template.Length && sb.Length < MaxOutputChars; i++)
        {
            char c = template[i];

            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                sb.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i++;
                continue;
            }

            if (c != '{')
            {
                sb.Append(c);
                continue;
            }

            int end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                sb.Append(c); // dangling brace — emit literally
                continue;
            }

            string token = template[(i + 1)..end].Trim();
            if (token.Length == 0)
            {
                sb.Append("{}"); // literal empty pair (JSON), not a token
            }
            else
            {
                sb.Append(Sanitize(Lookup(token, e)));
            }

            i = end;
        }

        return sb.Length > MaxOutputChars ? sb.ToString(0, MaxOutputChars) : sb.ToString();
    }

    private static string Lookup(string token, SyslogEvent e)
    {
        switch (token.ToLowerInvariant())
        {
            case "received" or "received_utc":
                return e.ReceivedUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            case "event_time":
                return e.EventUtc?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            case "event_id":
                return e.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "device_id":
                return e.DeviceId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            case "stream_count":
                return e.StreamIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!ConditionFields.TryResolve(token, out ConditionFieldInfo? field))
        {
            return string.Empty;
        }

        IReadOnlyList<string> values = EventFieldReader.Values(field, e);
        return values.Count == 0 ? string.Empty : string.Join(", ", values);
    }

    private static string Sanitize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
