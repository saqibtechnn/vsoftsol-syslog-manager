using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>
/// Validates a single <see cref="RuleAction"/> — the first of two layers (every executor
/// re-checks its own inputs at run time). Extracted from <see cref="RuleCompiler"/> so the
/// Phase 8 <c>AlertCompiler</c> validates the same action model the same way, with no
/// behaviour drift between the rule path and the alert path.
/// </summary>
internal static class RuleActionValidator
{
    internal const int MaxActionsPerRule = 20;

    private static readonly string[] AllowedHttpMethods = ["POST", "PUT", "PATCH"];

    public static void Validate(RuleAction action, string label, RuleCompileOptions options, List<string> errors)
    {
        switch (action)
        {
            case SendEmailAction email:
                if (string.IsNullOrWhiteSpace(email.Host))
                {
                    errors.Add($"{label}: SMTP host is required.");
                }

                if (email.Port is < 1 or > 65535)
                {
                    errors.Add($"{label}: SMTP port must be 1-65535.");
                }

                if (email.To.Count == 0 || email.To.Any(t => !LooksLikeEmail(t)))
                {
                    errors.Add($"{label}: at least one valid recipient address is required.");
                }

                if (!LooksLikeEmail(email.From))
                {
                    errors.Add($"{label}: a valid 'from' address is required.");
                }

                Template(label, email.Subject, errors);
                Template(label, email.Body, errors);
                break;

            case HttpWebhookAction webhook:
                if (!Uri.TryCreate(webhook.Url, UriKind.Absolute, out Uri? uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"{label}: webhook URL must be an absolute http or https URL.");
                }

                if (!AllowedHttpMethods.Contains(webhook.Method, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{label}: method must be one of {string.Join(", ", AllowedHttpMethods)}.");
                }

                if (webhook.TimeoutSeconds is < 1 or > 120)
                {
                    errors.Add($"{label}: timeout must be 1-120 seconds.");
                }

                Template(label, webhook.BodyTemplate, errors);
                foreach (string headerValue in webhook.Headers.Values)
                {
                    Template(label, headerValue, errors);
                }

                break;

            case RunScriptAction script:
                ValidateExecutablePath(label, script.ExecutablePath, options, errors);
                if (script.TimeoutSeconds is < 1 or > 3600)
                {
                    errors.Add($"{label}: timeout must be 1-3600 seconds.");
                }

                foreach (string arg in script.Arguments)
                {
                    Template(label, arg, errors);
                }

                break;

            case ForwardSyslogAction forward:
                if (string.IsNullOrWhiteSpace(forward.Host))
                {
                    errors.Add($"{label}: forward host is required.");
                }

                if (forward.Port is < 1 or > 65535)
                {
                    errors.Add($"{label}: forward port must be 1-65535.");
                }

                if (IsLocalEndpoint(forward.Host, forward.Port, options))
                {
                    errors.Add($"{label}: this target is one of the collector's own listeners — forwarding there would loop.");
                }

                Template(label, forward.Template, errors);
                break;

            case WriteToFileAction file:
                ValidateFileName(label, file.FileName, errors);
                if (file.RotateAtBytes < 4096)
                {
                    errors.Add($"{label}: rotate size must be at least 4096 bytes.");
                }

                Template(label, file.LineTemplate, errors);
                break;

            case WriteToOdbcAction odbc:
                if (string.IsNullOrWhiteSpace(odbc.ConnectionString))
                {
                    errors.Add($"{label}: ODBC connection string is required.");
                }

                if (!IsIdentifier(odbc.TableName))
                {
                    errors.Add($"{label}: table name must be a plain identifier.");
                }

                if (odbc.ColumnMap.Count == 0)
                {
                    errors.Add($"{label}: at least one column mapping is required.");
                }

                foreach ((string column, string template) in odbc.ColumnMap)
                {
                    if (!IsIdentifier(column))
                    {
                        errors.Add($"{label}: column name '{column}' must be a plain identifier.");
                    }

                    Template(label, template, errors);
                }

                break;

            case AddTagAction tag:
                if (string.IsNullOrWhiteSpace(tag.Tag))
                {
                    errors.Add($"{label}: tag value is required.");
                }

                Template(label, tag.Tag, errors);
                break;

            case RouteToStreamAction route:
                if (route.StreamId <= 0)
                {
                    errors.Add($"{label}: choose a stream to route to.");
                }

                break;

            case RaiseNotificationAction notify:
                if (string.IsNullOrWhiteSpace(notify.Title))
                {
                    errors.Add($"{label}: notification title is required.");
                }

                Template(label, notify.Title, errors);
                Template(label, notify.Body, errors);
                break;

            case SuppressAction:
                break;

            default:
                errors.Add($"{label}: unknown action '{action.GetType().Name}'.");
                break;
        }

        if (action.Throttle.MaxPerWindow < 0 || action.Throttle.WindowSeconds < 0 || action.Throttle.CooldownSeconds < 0)
        {
            errors.Add($"{label}: throttle values cannot be negative.");
        }
    }

    private static void Template(string label, string? template, List<string> errors)
    {
        if (!FieldTemplate.TryValidate(template, out string? error))
        {
            errors.Add($"{label}: {error}");
        }
    }

    private static void ValidateExecutablePath(string label, string path, RuleCompileOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{label}: executable path is required.");
            return;
        }

        if (path.Contains("..", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            errors.Add($"{label}: executable must be an absolute path with no '..'.");
            return;
        }

        if (options.ScriptAllowListDirectories.Count == 0)
        {
            errors.Add($"{label}: no script directories are allow-listed; an administrator must configure one first.");
            return;
        }

        string full = Path.GetFullPath(path);
        bool underAllowed = options.ScriptAllowListDirectories
            .Select(Path.GetFullPath)
            .Any(dir => full.StartsWith(EnsureTrailingSeparator(dir), StringComparison.OrdinalIgnoreCase));
        if (!underAllowed)
        {
            errors.Add($"{label}: '{path}' is not under an allow-listed script directory.");
        }
    }

    private static void ValidateFileName(string label, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add($"{label}: file name is required.");
            return;
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal)
            || name.StartsWith('/') || name.StartsWith('\\')
            || name.Contains("\\\\", StringComparison.Ordinal))
        {
            errors.Add($"{label}: file name must be relative with no '..', drive, or UNC prefix.");
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(baseName))
        {
            errors.Add($"{label}: '{name}' uses a reserved device name.");
        }
    }

    private static bool IsLocalEndpoint(string host, int port, RuleCompileOptions options)
    {
        string candidate = $"{host.Trim()}:{port}";
        return options.LocalSyslogEndpoints.Any(e => string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            return false;
        }

        int at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 3
            && value.IndexOf('.', at) > at;
    }

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.'))
            {
                return false;
            }
        }

        return char.IsAsciiLetter(value[0]) || value[0] == '_';
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
