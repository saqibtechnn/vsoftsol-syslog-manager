using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Rules.Rules;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Rules;

public sealed record RuleActionResult(bool Ok, string Message, IReadOnlyList<string> Errors)
{
    public static RuleActionResult Success(string message) => new(true, message, []);

    public static RuleActionResult Fail(string message, IReadOnlyList<string>? errors = null) =>
        new(false, message, errors ?? []);
}

/// <summary>What a rule / action would do for a sample message — no side effects (PHASE_07 item 7).</summary>
public sealed record RuleDryRun(long RuleId, string RuleName, bool Matched, bool Enabled, IReadOnlyList<string> WouldRun, string Reason);

/// <summary>
/// Rule CRUD from the UI (PHASE_07 item 1), the rule tester (item 7), and the per-action
/// "Test" buttons. Rules are validated with the same <see cref="RuleCompiler"/> the ingest
/// engine uses, so "it saved" means "it will run". Administrator/Operator only; every
/// mutation is audited.
/// </summary>
public sealed class RuleAdminService
{
    private readonly SqliteRuleStore _rules;
    private readonly RuleSetProvider _provider;
    private readonly ActionExecutorRegistry _executors;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;
    private readonly SecretResolver _secrets;
    private readonly NotificationSink _notifications;
    private readonly ActionExecutorOptions _actionOptions;
    private readonly RuleCompiler _compiler;

    public RuleAdminService(
        SqliteRuleStore rules,
        RuleSetProvider provider,
        ActionExecutorRegistry executors,
        SqliteAuditLog audit,
        CurrentUserAccessor users,
        SecretResolver secrets,
        NotificationSink notifications,
        Microsoft.Extensions.Options.IOptions<ActionExecutorOptions> actionOptions)
    {
        _rules = rules;
        _provider = provider;
        _executors = executors;
        _audit = audit;
        _users = users;
        _secrets = secrets;
        _notifications = notifications;
        _actionOptions = actionOptions.Value;
        _compiler = new RuleCompiler(new RuleCompileOptions(
            _actionOptions.ScriptAllowListDirectories,
            _actionOptions.FileActionBaseDirectory,
            _actionOptions.LocalSyslogEndpoints));
    }

    public Task<IReadOnlyList<RuleRow>> ListAsync(CancellationToken ct) => _rules.ListAllAsync(ct);

    public Task<RuleRow?> GetAsync(long id, CancellationToken ct) => _rules.GetAsync(id, ct);

    public static IReadOnlyList<DefaultRuleTemplates.Template> Templates => DefaultRuleTemplates.All;

    public async Task<RuleActionResult> SaveAsync(RuleDefinition rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return RuleActionResult.Fail("You do not have permission to change rules.");
        }

        RuleCompileResult compiled = _compiler.Compile(rule);
        if (!compiled.Success)
        {
            return RuleActionResult.Fail("The rule has a problem.", compiled.Errors);
        }

        if (rule.RuleId == 0)
        {
            long id = await _rules.CreateAsync(rule, user.UserName, ct).ConfigureAwait(false);
            await Audit(AuditActions.RuleCreate, id, rule.Name, user).ConfigureAwait(false);
        }
        else
        {
            if (!await _rules.UpdateAsync(rule, user.UserName, ct).ConfigureAwait(false))
            {
                return RuleActionResult.Fail("That rule no longer exists.");
            }

            await Audit(AuditActions.RuleUpdate, rule.RuleId, rule.Name, user).ConfigureAwait(false);
        }

        return RuleActionResult.Success("Rule saved.");
    }

    public async Task<RuleActionResult> SetEnabledAsync(long id, bool enabled, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return RuleActionResult.Fail("You do not have permission to change rules.");
        }

        if (!await _rules.SetEnabledAsync(id, enabled, user.UserName, ct).ConfigureAwait(false))
        {
            return RuleActionResult.Fail("That rule no longer exists.");
        }

        await Audit(enabled ? AuditActions.RuleEnable : AuditActions.RuleDisable, id, string.Empty, user).ConfigureAwait(false);
        return RuleActionResult.Success(enabled ? "Rule enabled." : "Rule disabled.");
    }

    public async Task<RuleActionResult> DeleteAsync(long id, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role != Role.Administrator)
        {
            return RuleActionResult.Fail("Only an administrator can delete a rule.");
        }

        if (!await _rules.DeleteAsync(id, ct).ConfigureAwait(false))
        {
            return RuleActionResult.Fail("That rule cannot be deleted (it may be a system rule).");
        }

        await Audit(AuditActions.RuleDelete, id, string.Empty, user).ConfigureAwait(false);
        return RuleActionResult.Success("Rule deleted.");
    }

    public async Task<RuleActionResult> CloneTemplateAsync(int templateIndex, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return RuleActionResult.Fail("You do not have permission to add rules.");
        }

        if (templateIndex < 0 || templateIndex >= DefaultRuleTemplates.All.Count)
        {
            return RuleActionResult.Fail("That template does not exist.");
        }

        RuleDefinition clone = DefaultRuleTemplates.All[templateIndex].Rule;
        clone.RuleId = 0;
        clone.Enabled = false;
        clone.Name = await UniqueName(clone.Name, ct).ConfigureAwait(false);

        long id = await _rules.CreateAsync(clone, user.UserName, ct).ConfigureAwait(false);
        await Audit(AuditActions.RuleCreate, id, $"from template '{clone.Name}'", user).ConfigureAwait(false);
        return RuleActionResult.Success($"Created '{clone.Name}' — fill in the destination and enable it.");
    }

    /// <summary>PHASE_07 item 7 — dry-run every rule against a sample message. Executes nothing.</summary>
    public async Task<IReadOnlyList<RuleDryRun>> DryRunAsync(SyslogEvent sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sample);
        IReadOnlyList<RuleRow> rows = await _rules.ListAllAsync(ct).ConfigureAwait(false);
        RuleSet ruleSet = await _provider.GetAsync(ct).ConfigureAwait(false);

        var matchedIds = ruleSet.Match(sample, [], DateTimeOffset.UtcNow).Select(r => r.RuleId).ToHashSet();

        return rows.Select(row =>
        {
            RuleCompileResult compiled = _compiler.Compile(row.ToDefinition());
            if (!compiled.Success)
            {
                return new RuleDryRun(row.RuleId, row.Name, false, row.Enabled, [], "rule does not compile: " + string.Join("; ", compiled.Errors));
            }

            bool matched = row.Enabled && matchedIds.Contains(row.RuleId);
            IReadOnlyList<string> wouldRun = matched
                ? row.Actions.Select(RuleActionInfo.Label).ToList()
                : [];
            string reason = !row.Enabled ? "disabled"
                : matched ? "filter matched — these actions would run (nothing executed)"
                : "filter did not match this message";
            return new RuleDryRun(row.RuleId, row.Name, matched, row.Enabled, wouldRun, reason);
        }).ToList();
    }

    /// <summary>
    /// The per-action "Test" button. <paramref name="reallyExecute"/> false → a dry render
    /// only; true → actually sends to the configured destination (the UI confirms first).
    /// </summary>
    public async Task<RuleActionResult> TestActionAsync(RuleAction action, SyslogEvent sample, bool reallyExecute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return RuleActionResult.Fail("You do not have permission to test actions.");
        }

        if (!reallyExecute)
        {
            return RuleActionResult.Success($"Dry run: '{RuleActionInfo.Label(action)}' would run for this sample. Nothing was sent.");
        }

        var context = new ActionContext(0, "test", action, sample, _secrets, _notifications, _actionOptions);
        ActionResult result = await _executors.ExecuteAsync(context, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(AuditActions.ActionTested, user.UserName, "rule", "0",
            Detail: $"{RuleActionInfo.Kind(action)}: {result.Detail}"), CancellationToken.None).ConfigureAwait(false);

        return result.Ok
            ? RuleActionResult.Success($"Test succeeded: {result.Detail}")
            : RuleActionResult.Fail($"Test failed: {result.Detail}");
    }

    private async Task<string> UniqueName(string baseName, CancellationToken ct)
    {
        IReadOnlyList<RuleRow> existing = await _rules.ListAllAsync(ct).ConfigureAwait(false);
        var taken = existing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }

    private Task Audit(string action, long ruleId, string name, CurrentUser user) =>
        _audit.AppendAsync(
            new AuditEntry(action, user.UserName, "rule",
                ruleId.ToString(System.Globalization.CultureInfo.InvariantCulture), Detail: name),
            CancellationToken.None);
}
