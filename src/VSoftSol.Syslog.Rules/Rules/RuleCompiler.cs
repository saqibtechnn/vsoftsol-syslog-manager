using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Conditions;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Rules;

/// <summary>
/// Host-supplied limits the compiler needs to validate actions that touch the local
/// machine or network (PHASE_07 security). Supplied by the composition root, never by a
/// rule author.
/// </summary>
/// <param name="ScriptAllowListDirectories">Absolute directories a <c>RunScript</c> executable may live under.</param>
/// <param name="FileActionBaseDirectory">Absolute base directory <c>WriteToFile</c> is confined to.</param>
/// <param name="LocalSyslogEndpoints">The collector's own listener endpoints — a forward target here is a loop.</param>
public sealed record RuleCompileOptions(
    IReadOnlyList<string> ScriptAllowListDirectories,
    string? FileActionBaseDirectory,
    IReadOnlyList<string> LocalSyslogEndpoints)
{
    public static RuleCompileOptions Empty { get; } = new([], null, []);
}

/// <summary>The outcome of compiling a <see cref="RuleDefinition"/>.</summary>
public sealed record RuleCompileResult
{
    public bool Success => Errors.Count == 0 && Rule is not null;

    public CompiledRule? Rule { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static RuleCompileResult Ok(CompiledRule rule) => new() { Rule = rule };

    public static RuleCompileResult Fail(IReadOnlyList<string> errors) => new() { Errors = errors };
}

/// <summary>
/// Validates a <see cref="RuleDefinition"/> and compiles it once for repeated evaluation on
/// the ingest path. Action validation here is the first of two layers — every executor
/// re-checks its own inputs at run time (defence in depth for the security matrices).
/// </summary>
public sealed class RuleCompiler
{
    private static readonly string[] AllowedHttpMethods = ["POST", "PUT", "PATCH"];
    private const int MaxActionsPerRule = 20;

    private readonly ConditionCompiler _conditions = new();
    private readonly RuleCompileOptions _options;

    public RuleCompiler(RuleCompileOptions? options = null) => _options = options ?? RuleCompileOptions.Empty;

    /// <summary>
    /// Compiles a whole rule set: each rule is compiled, the failures are collected (never
    /// fatal), and the survivors are ordered by priority then id — exactly what the
    /// ingest-path <see cref="RuleSet"/> expects.
    /// </summary>
    public CompiledRuleSet CompileSet(IEnumerable<RuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var compiled = new List<CompiledRule>();
        var errors = new List<RuleCompileError>();

        foreach (RuleDefinition rule in rules)
        {
            RuleCompileResult result = Compile(rule);
            if (result.Success)
            {
                compiled.Add(result.Rule!);
            }
            else
            {
                errors.Add(new RuleCompileError(rule.RuleId, rule.Name, result.Errors));
            }
        }

        compiled.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.RuleId.CompareTo(b.RuleId));
        return new CompiledRuleSet(compiled, errors);
    }

    public RuleCompileResult Compile(RuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add("The rule needs a name.");
        }

        bool alwaysMatches = rule.Filter is null || rule.Filter.Children.Count == 0;
        CompiledCondition filter = CompiledCondition.MatchNothing;
        if (!alwaysMatches)
        {
            ConditionCompileResult compiled = _conditions.Compile(rule.Filter);
            if (compiled.Success)
            {
                filter = compiled.Condition!;
            }
            else
            {
                errors.AddRange(compiled.Errors.Select(e => "Filter: " + e));
            }
        }

        if (rule.Actions.Count == 0)
        {
            errors.Add("The rule has no actions.");
        }
        else if (rule.Actions.Count > MaxActionsPerRule)
        {
            errors.Add($"Too many actions ({rule.Actions.Count}); the limit is {MaxActionsPerRule}.");
        }

        for (int i = 0; i < rule.Actions.Count; i++)
        {
            ValidateAction(rule.Actions[i], $"Action {i + 1}", errors);
        }

        if (rule.Window is { } w)
        {
            if (w.StartMinute is < 0 or > 1439 || w.EndMinute is < 0 or > 1439)
            {
                errors.Add("Time window minutes must be between 0 and 1439.");
            }
        }

        if (rule.Escalation is { } esc)
        {
            if (esc.Threshold < 2)
            {
                errors.Add("Escalation threshold must be at least 2.");
            }

            if (esc.WindowSeconds < 1)
            {
                errors.Add("Escalation window must be at least 1 second.");
            }

            if (esc.EscalationActions.Count == 0)
            {
                errors.Add("Escalation is enabled but has no actions.");
            }

            for (int i = 0; i < esc.EscalationActions.Count; i++)
            {
                ValidateAction(esc.EscalationActions[i], $"Escalation action {i + 1}", errors);
            }
        }

        if (errors.Count > 0)
        {
            return RuleCompileResult.Fail(errors);
        }

        return RuleCompileResult.Ok(new CompiledRule(
            rule.RuleId,
            rule.Name.Trim(),
            rule.Priority,
            filter,
            alwaysMatches,
            rule.Actions,
            rule.Window,
            rule.DeviceGroupIds,
            rule.Escalation));
    }

    private void ValidateAction(RuleAction action, string label, List<string> errors)
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
                ValidateExecutablePath(label, script.ExecutablePath, errors);
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

                if (IsLocalEndpoint(forward.Host, forward.Port))
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

    private void ValidateExecutablePath(string label, string path, List<string> errors)
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

        if (_options.ScriptAllowListDirectories.Count == 0)
        {
            errors.Add($"{label}: no script directories are allow-listed; an administrator must configure one first.");
            return;
        }

        string full = Path.GetFullPath(path);
        bool underAllowed = _options.ScriptAllowListDirectories
            .Select(Path.GetFullPath)
            .Any(dir => full.StartsWith(EnsureTrailingSeparator(dir), StringComparison.OrdinalIgnoreCase));
        if (!underAllowed)
        {
            errors.Add($"{label}: '{path}' is not under an allow-listed script directory.");
        }
    }

    private void ValidateFileName(string label, string name, List<string> errors)
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

    private bool IsLocalEndpoint(string host, int port)
    {
        string candidate = $"{host.Trim()}:{port}";
        return _options.LocalSyslogEndpoints.Any(e => string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase));
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
