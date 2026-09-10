using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Rules.Conditions;
using VSoftSol.Syslog.Rules.Rules;

namespace VSoftSol.Syslog.Rules.Alerts;

/// <summary>The outcome of compiling one <see cref="AlertDefinition"/>.</summary>
public sealed record AlertCompileResult
{
    public bool Success => Errors.Count == 0 && Alert is not null;

    public CompiledAlert? Alert { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static AlertCompileResult Ok(CompiledAlert alert) => new() { Alert = alert };

    public static AlertCompileResult Fail(IReadOnlyList<string> errors) => new() { Errors = errors };
}

/// <summary>
/// Validates an <see cref="AlertDefinition"/> and compiles it for the scheduler (PHASE_08).
/// Reuses the Phase 6 <see cref="ConditionCompiler"/> for the filter and the shared
/// <see cref="RuleActionValidator"/> for the actions, so an alert's filter and actions are
/// held to exactly the same rules as a Phase 7 rule's — no drift.
/// </summary>
public sealed class AlertCompiler
{
    /// <summary>Hard ceiling on the look-back window: 30 days.</summary>
    internal const int MaxWindowSeconds = 30 * 24 * 60 * 60;

    private readonly ConditionCompiler _conditions = new();
    private readonly RuleCompileOptions _options;

    public AlertCompiler(RuleCompileOptions? options = null) => _options = options ?? RuleCompileOptions.Empty;

    /// <summary>Compiles a whole set: failures are collected (never fatal), survivors kept in id order.</summary>
    public CompiledAlertSet CompileSet(IEnumerable<AlertDefinition> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        var compiled = new List<CompiledAlert>();
        var errors = new List<AlertCompileError>();

        foreach (AlertDefinition alert in alerts)
        {
            AlertCompileResult result = Compile(alert);
            if (result.Success)
            {
                compiled.Add(result.Alert!);
            }
            else
            {
                errors.Add(new AlertCompileError(alert.AlertId, alert.Name, result.Errors));
            }
        }

        compiled.Sort((a, b) => a.AlertId.CompareTo(b.AlertId));
        return new CompiledAlertSet(compiled, errors);
    }

    public AlertCompileResult Compile(AlertDefinition alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(alert.Name))
        {
            errors.Add("The alert needs a name.");
        }

        if (alert.WindowSeconds is < 1 or > MaxWindowSeconds)
        {
            errors.Add($"The window must be between 1 second and {MaxWindowSeconds / 86400} days.");
        }

        if (alert.IntervalSeconds < 1)
        {
            errors.Add("The evaluation interval must be at least 1 second.");
        }

        if (alert.ReNotifySeconds < 0)
        {
            errors.Add("The re-notify interval cannot be negative.");
        }

        // Filter — compiled for every type except DeviceSilent, which is defined by absence
        // of traffic, not by message content.
        bool usesFilter = alert.Type != AlertEvaluationType.DeviceSilent;
        bool alwaysMatches = alert.Filter is null || alert.Filter.Children.Count == 0;
        CompiledCondition filter = CompiledCondition.MatchNothing;
        if (usesFilter && !alwaysMatches)
        {
            ConditionCompileResult compiled = _conditions.Compile(alert.Filter);
            if (compiled.Success)
            {
                filter = compiled.Condition!;
            }
            else
            {
                errors.AddRange(compiled.Errors.Select(e => "Filter: " + e));
            }
        }

        // Group-by field.
        string? groupBy = NormaliseGroupBy(alert.GroupByField);
        switch (alert.Type)
        {
            case AlertEvaluationType.DistinctCount when groupBy is null:
                errors.Add("A distinct-count alert must choose the field whose distinct values it counts.");
                break;
            case AlertEvaluationType.Threshold or AlertEvaluationType.DistinctCount when groupBy is not null:
                ValidateGroupByField(groupBy, errors);
                break;
        }

        // Threshold.
        if (alert.Type is AlertEvaluationType.Threshold or AlertEvaluationType.DistinctCount && alert.Threshold < 1)
        {
            errors.Add("The threshold must be at least 1.");
        }

        // Actions — optional (an instance is still visible in the notification centre), but
        // any action present must be valid.
        if (alert.Actions.Count > RuleActionValidator.MaxActionsPerRule)
        {
            errors.Add($"Too many actions ({alert.Actions.Count}); the limit is {RuleActionValidator.MaxActionsPerRule}.");
        }

        for (int i = 0; i < alert.Actions.Count; i++)
        {
            RuleActionValidator.Validate(alert.Actions[i], $"Action {i + 1}", _options, errors);
        }

        if (errors.Count > 0)
        {
            return AlertCompileResult.Fail(errors);
        }

        return AlertCompileResult.Ok(new CompiledAlert(
            alert.AlertId,
            alert.Name.Trim(),
            alert.Severity,
            alert.Type,
            filter,
            !usesFilter || alwaysMatches,
            alert.WindowSeconds,
            alert.IntervalSeconds,
            alert.Type is AlertEvaluationType.Threshold or AlertEvaluationType.DistinctCount ? groupBy : null,
            alert.Threshold,
            alert.Actions,
            [.. alert.DeviceGroupIds],
            [.. alert.StreamIds],
            alert.ReNotifySeconds,
            alert.AutoResolve));
    }

    private static string? NormaliseGroupBy(string? field)
    {
        if (string.IsNullOrWhiteSpace(field) || string.Equals(field.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return field.Trim();
    }

    private static void ValidateGroupByField(string groupBy, List<string> errors)
    {
        if (string.Equals(groupBy, "message", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Group by a field with a bounded set of values (host, source IP, app…), not the message text — use a filter for message content.");
            return;
        }

        if (!ConditionFields.TryResolve(groupBy, out _))
        {
            errors.Add($"Unknown group-by field '{groupBy}'.");
        }
    }
}
