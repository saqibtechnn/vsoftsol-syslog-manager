using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Rules.Alerts;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Alerts;

/// <summary>The result of an alert admin operation.</summary>
public sealed record AlertActionResult(bool Ok, string Message, IReadOnlyList<string> Errors)
{
    public static AlertActionResult Success(string message) => new(true, message, []);

    public static AlertActionResult Fail(string message, IReadOnlyList<string>? errors = null) => new(false, message, errors ?? []);
}

/// <summary>The "would have fired" preview (PHASE_08 UX gate).</summary>
/// <param name="Count">How many times the alert would have fired over the look-back.</param>
/// <param name="Approximate">
/// True when the historical scan hit its row cap before covering the whole look-back —
/// <see cref="Count"/> is then a lower bound, never a statistical estimate (v1.1 — P8-1;
/// every alert type is now a full, bucket-by-bucket replay, never sampled).
/// </param>
/// <param name="Detail">A human sentence for the editor.</param>
public sealed record AlertPreview(int Count, bool Approximate, string Detail);

/// <summary>One triggering event shown on the history detail, already scope-checked.</summary>
public sealed record TriggerEventView(long EventId, DateTimeOffset ReceivedUtc, string? Hostname, string Message, bool Visible);

/// <summary>An open or historical instance joined with its alert's name — for the list / history pages.</summary>
public sealed record AlertInstanceView(AlertInstance Instance, string AlertName, AlertEvaluationType Type);

/// <summary>
/// Alert CRUD + the ack/resolve lifecycle from the UI (PHASE_08). Role is checked
/// <b>at the service</b>: Administrator/Operator may create and edit; Read-Only and Auditor
/// may not acknowledge or resolve. Instance listings are filtered by the caller's
/// <see cref="UserScope"/>, and triggering events go through <see cref="ScopedEventReader"/>
/// so a notification never surfaces an event from a stream the viewer cannot see.
/// </summary>
public sealed class AlertAdminService
{
    private readonly SqliteAlertStore _alerts;
    private readonly SqliteAlertInstanceStore _instances;
    private readonly SqliteAlertWindowReader _reader;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;
    private readonly ScopedEventReader _events;
    private readonly SqliteSavedSearchStore _savedSearches;
    private readonly SqliteDeviceStore _devices;
    private readonly VSoftSol.Syslog.Data.Users.SqliteUserStore _userStore;
    private readonly AlertCompiler _compiler;
    private readonly int _previewScanCap;

    public AlertAdminService(
        SqliteAlertStore alerts,
        SqliteAlertInstanceStore instances,
        SqliteAlertWindowReader reader,
        SqliteAuditLog audit,
        CurrentUserAccessor users,
        ScopedEventReader events,
        SqliteSavedSearchStore savedSearches,
        SqliteDeviceStore devices,
        VSoftSol.Syslog.Data.Users.SqliteUserStore userStore,
        Microsoft.Extensions.Options.IOptions<ActionExecutorOptions> actionOptions,
        Microsoft.Extensions.Options.IOptions<AlertEvaluationOptions> evaluationOptions)
    {
        _alerts = alerts;
        _instances = instances;
        _reader = reader;
        _audit = audit;
        _users = users;
        _events = events;
        _savedSearches = savedSearches;
        _devices = devices;
        _userStore = userStore;
        ActionExecutorOptions a = actionOptions.Value;
        _compiler = new AlertCompiler(new VSoftSol.Syslog.Rules.Rules.RuleCompileOptions(
            a.ScriptAllowListDirectories, a.FileActionBaseDirectory, a.LocalSyslogEndpoints));
        // v1.1 — P8-1: the same row cap the live scheduler uses for one tick's window scan
        // (AlertEvaluationOptions.MaxWindowScan), reused here to bound the preview's single
        // whole-look-back scan.
        _previewScanCap = Math.Max(1, evaluationOptions.Value.MaxWindowScan);
    }

    public static IReadOnlyList<VSoftSol.Syslog.Data.Seed.DefaultAlertTemplates.Template> Templates =>
        VSoftSol.Syslog.Data.Seed.DefaultAlertTemplates.All;

    public Task<IReadOnlyList<AlertRow>> ListDefinitionsAsync(CancellationToken ct) => _alerts.ListAllAsync(ct);

    public Task<AlertRow?> GetAsync(long id, CancellationToken ct) => _alerts.GetAsync(id, ct);

    // ---------------------------------------------------------------- definitions

    public async Task<AlertActionResult> SaveAsync(AlertDefinition alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (!await CanEditAsync().ConfigureAwait(false))
        {
            return AlertActionResult.Fail("You do not have permission to change alerts.");
        }

        AlertCompileResult compiled = _compiler.Compile(alert);
        if (!compiled.Success)
        {
            return AlertActionResult.Fail("The alert has a problem.", compiled.Errors);
        }

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (alert.AlertId == 0)
        {
            long id = await _alerts.CreateAsync(alert, user.UserName, ct).ConfigureAwait(false);
            await Audit(AuditActions.AlertCreate, id, alert.Name, user).ConfigureAwait(false);
        }
        else
        {
            if (!await _alerts.UpdateAsync(alert, user.UserName, ct).ConfigureAwait(false))
            {
                return AlertActionResult.Fail("That alert no longer exists.");
            }

            await Audit(AuditActions.AlertUpdate, alert.AlertId, alert.Name, user).ConfigureAwait(false);
        }

        return AlertActionResult.Success("Alert saved.");
    }

    public async Task<AlertActionResult> SetEnabledAsync(long id, bool enabled, CancellationToken ct)
    {
        if (!await CanEditAsync().ConfigureAwait(false))
        {
            return AlertActionResult.Fail("You do not have permission to change alerts.");
        }

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (!await _alerts.SetEnabledAsync(id, enabled, user.UserName, ct).ConfigureAwait(false))
        {
            return AlertActionResult.Fail("That alert no longer exists.");
        }

        await Audit(enabled ? AuditActions.AlertEnable : AuditActions.AlertDisable, id, string.Empty, user).ConfigureAwait(false);
        return AlertActionResult.Success(enabled ? "Alert enabled." : "Alert disabled.");
    }

    public async Task<AlertActionResult> DeleteAsync(long id, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role != Role.Administrator)
        {
            return AlertActionResult.Fail("Only an administrator can delete an alert.");
        }

        if (!await _alerts.DeleteAsync(id, ct).ConfigureAwait(false))
        {
            return AlertActionResult.Fail("That alert cannot be deleted (it may be a system alert).");
        }

        await Audit(AuditActions.AlertDelete, id, string.Empty, user).ConfigureAwait(false);
        return AlertActionResult.Success("Alert deleted.");
    }

    public async Task<AlertActionResult> CloneTemplateAsync(int templateIndex, CancellationToken ct)
    {
        if (!await CanEditAsync().ConfigureAwait(false))
        {
            return AlertActionResult.Fail("You do not have permission to add alerts.");
        }

        IReadOnlyList<VSoftSol.Syslog.Data.Seed.DefaultAlertTemplates.Template> all = Templates;
        if (templateIndex < 0 || templateIndex >= all.Count)
        {
            return AlertActionResult.Fail("That template does not exist.");
        }

        AlertDefinition clone = all[templateIndex].Alert;
        clone.AlertId = 0;
        clone.Enabled = false;
        clone.Name = await UniqueNameAsync(clone.Name, ct).ConfigureAwait(false);

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        long id = await _alerts.CreateAsync(clone, user.UserName, ct).ConfigureAwait(false);
        await Audit(AuditActions.AlertCreate, id, $"from template '{clone.Name}'", user).ConfigureAwait(false);
        return AlertActionResult.Success($"Created '{clone.Name}' — review it and enable it.");
    }

    // ---------------------------------------------------------------- preview

    /// <summary>PHASE_08 UX gate: "this would have fired N times in the last 7 days".</summary>
    public async Task<AlertPreview> PreviewAsync(AlertDefinition alert, TimeSpan? lookback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        AlertCompileResult compiled = _compiler.Compile(alert);
        if (!compiled.Success)
        {
            return new AlertPreview(0, false, "Fix the errors above to see a preview.");
        }

        TimeSpan window = lookback ?? TimeSpan.FromDays(7);
        CompiledAlert c = compiled.Alert!;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (c.Type is AlertEvaluationType.DeviceSilent)
        {
            return new AlertPreview(0, true, "Device-silent alerts fire when a device stops sending — save it and watch the alert list.");
        }

        IReadOnlyList<long> deviceIds = c.DeviceGroupIds.Count > 0
            ? await _reader.ResolveDeviceIdsAsync(c.DeviceGroupIds, ct).ConfigureAwait(false)
            : [];

        if (c.AlwaysMatches && c.Type is AlertEvaluationType.Threshold && (c.GroupByField is null || SqliteAlertWindowReader.IsSqlGroupable(c.GroupByField)))
        {
            IReadOnlyList<(long Bucket, string? Group, long Count)> buckets = await _reader
                .PreviewBucketCountsAsync(now, window, c.WindowSeconds, c.GroupByField, deviceIds, c.StreamIds, ct)
                .ConfigureAwait(false);

            int breaches = buckets.Count(b => b.Count > c.Threshold);
            return new AlertPreview(breaches, false, Sentence(breaches, window, exact: true));
        }

        // v1.1 — P8-1 (docs/evidence/phase-08/known-issues.md): filtered / distinct-count /
        // absence alerts used to sample 24 windows and extrapolate. Every non-overlapping
        // WindowSeconds-long bucket across the whole look-back is now evaluated — a full
        // replay, not an estimate — via exactly one bounded scan of the raw events instead
        // of one scan per bucket, so this stays cheap even when there are many buckets.
        (int fired, bool truncated) = await FullReplayAsync(c, now, window, deviceIds, ct).ConfigureAwait(false);
        return new AlertPreview(fired, truncated, Sentence(fired, window, exact: !truncated));
    }

    /// <summary>
    /// Buckets the look-back into non-overlapping <c>WindowSeconds</c>-long slices and
    /// evaluates each one with the same <see cref="AlertEvaluator"/> the live scheduler
    /// uses, summing the breaching groups per bucket (a threshold alert with a group-by can
    /// breach in more than one group within the same bucket — each counts as its own fire,
    /// the same as <c>AlertEvaluationService.ReconcileAsync</c> opening one instance per
    /// breaching group). Capped at <see cref="_previewScanCap"/> total matching-or-not raw
    /// events read — the same cap the live scheduler applies to one tick's window scan,
    /// reused here to bound one scan across the whole look-back instead. A capped scan makes
    /// <see cref="AlertPreview.Count"/> a lower bound, never a statistical estimate.
    /// </summary>
    private async Task<(int Fired, bool Truncated)> FullReplayAsync(
        CompiledAlert c, DateTimeOffset now, TimeSpan window, IReadOnlyList<long> deviceIds, CancellationToken ct)
    {
        DateTimeOffset from = now - window;
        int bucketSeconds = Math.Max(1, c.WindowSeconds);
        int bucketCount = Math.Max(1, (int)(window.TotalSeconds / bucketSeconds));

        var counts = new Dictionary<string, long>?[bucketCount];
        var distinctValues = new HashSet<string>?[bucketCount];
        var anyMatch = new bool[bucketCount];

        // Whole-second Unix-epoch bucketing (memory: "SQLite time bucketing: use strftime
        // seconds" — the same pitfall this codebase already hit once). `from` and each
        // event's ReceivedUtc otherwise carry independent sub-second precision, so a tiny,
        // unavoidable clock drift between when a caller's data was written and this method's
        // own `now` could split two events a few milliseconds apart across a bucket boundary
        // that looks aligned at whole-second resolution. Integer seconds make the boundary
        // exact, matching PreviewBucketCountsAsync's own `strftime('%s', ...)` SQL bucketing
        // above so both branches of this method agree on what a "bucket" is.
        long fromSeconds = from.ToUnixTimeSeconds();

        // Ask for one more row than the cap: only then does "got more rows than the cap"
        // unambiguously mean "there were more matching rows than the cap", rather than
        // "there were exactly cap rows" (StreamWindowAsync's own SQL LIMIT would otherwise
        // make scanned > cap unreachable).
        int scanned = 0;
        bool truncated = false;
        await foreach (SyslogEvent e in _reader.StreamWindowAsync(from, now, deviceIds, c.StreamIds, _previewScanCap + 1, ct).ConfigureAwait(false))
        {
            if (++scanned > _previewScanCap)
            {
                truncated = true;
                break;
            }

            if (!c.FilterMatches(e))
            {
                continue;
            }

            int bucket = (int)((e.ReceivedUtc.ToUnixTimeSeconds() - fromSeconds) / bucketSeconds);
            if (bucket < 0 || bucket >= bucketCount)
            {
                continue;
            }

            if (c.Type is AlertEvaluationType.DistinctCount)
            {
                string? key = AlertGrouping.DistinctKeyFor(c.GroupByField!, e);
                if (key is not null)
                {
                    (distinctValues[bucket] ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
                }
            }
            else if (c.Type is AlertEvaluationType.Absence)
            {
                anyMatch[bucket] = true;
            }
            else
            {
                string group = AlertGrouping.KeyFor(c.GroupByField, e) ?? string.Empty;
                Dictionary<string, long> bucketCounts = counts[bucket] ??= new Dictionary<string, long>(StringComparer.Ordinal);
                bucketCounts[group] = bucketCounts.GetValueOrDefault(group) + 1;
            }
        }

        int fired = 0;
        for (int i = 0; i < bucketCount; i++)
        {
            AlertWindowData data = c.Type switch
            {
                AlertEvaluationType.DistinctCount => new AlertWindowData { DistinctValueCount = distinctValues[i]?.Count ?? 0 },
                AlertEvaluationType.Absence => new AlertWindowData { GroupCounts = [new GroupCount(null, anyMatch[i] ? 1 : 0, [])] },
                _ => new AlertWindowData
                {
                    GroupCounts = counts[i] is { } bucketCounts
                        ? [.. bucketCounts.Select(kv => new GroupCount(c.GroupByField is null ? null : kv.Key, kv.Value, []))]
                        : [],
                },
            };

            DateTimeOffset bucketEnd = from + TimeSpan.FromSeconds((long)(i + 1) * bucketSeconds);
            fired += AlertEvaluator.Evaluate(c, data, bucketEnd).Breaches.Count;
        }

        return (fired, truncated);
    }

    private static string Sentence(int count, TimeSpan window, bool exact)
    {
        string days = window.TotalDays >= 1 ? $"{window.TotalDays:F0} days" : $"{window.TotalHours:F0} hours";
        string times = $"{count} time{(count == 1 ? string.Empty : "s")}";

        if (exact)
        {
            return count == 0
                ? $"This would not have fired in the last {days}."
                : $"This would have fired {times} in the last {days}.";
        }

        // v1.1 — P8-1: "not exact" now only ever means the historical scan hit its row cap
        // before covering the whole look-back — a lower bound, never a sampled estimate.
        return $"This would have fired at least {times} in the last {days} (the scan hit its row cap before finishing).";
    }

    // ---------------------------------------------------------------- lifecycle

    public Task<AlertActionResult> AcknowledgeAsync(long instanceId, string? note, CancellationToken ct) =>
        TransitionAsync(instanceId, note, ack: true, ct);

    public Task<AlertActionResult> ResolveAsync(long instanceId, string? note, CancellationToken ct) =>
        TransitionAsync(instanceId, note, ack: false, ct);

    private async Task<AlertActionResult> TransitionAsync(long instanceId, string? note, bool ack, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return AlertActionResult.Fail("You do not have permission to acknowledge or resolve alerts.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool ok = ack
            ? await _instances.AcknowledgeAsync(instanceId, user.UserName, note, now, ct).ConfigureAwait(false)
            : await _instances.ResolveAsync(instanceId, user.UserName, note, auto: false, now, ct).ConfigureAwait(false);

        if (!ok)
        {
            return AlertActionResult.Fail(ack ? "That alert is not in a state that can be acknowledged." : "That alert is already resolved.");
        }

        AlertInstance? instance = await _instances.GetAsync(instanceId, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(
            ack ? AuditActions.AlertAcknowledged : AuditActions.AlertResolved,
            user.UserName, "alert",
            instance?.AlertId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Detail: $"instance {instanceId}{(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}"), CancellationToken.None).ConfigureAwait(false);

        return AlertActionResult.Success(ack ? "Acknowledged." : "Resolved.");
    }

    // ---------------------------------------------------------------- listings (scope-filtered)

    public async Task<IReadOnlyList<AlertInstanceView>> ListOpenAsync(CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        IReadOnlyList<AlertInstance> open = await _instances.ListOpenAsync(ct).ConfigureAwait(false);
        return await JoinAndScopeAsync(open, user.Scope, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlertInstanceView>> ListHistoryAsync(AlertHistoryQuery query, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        IReadOnlyList<AlertInstance> rows = await _instances.ListHistoryAsync(query, ct).ConfigureAwait(false);
        return await JoinAndScopeAsync(rows, user.Scope, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<NotificationLevel, int>> OpenCountsBySeverityAsync(CancellationToken ct)
    {
        IReadOnlyList<AlertInstanceView> open = await ListOpenAsync(ct).ConfigureAwait(false);
        return open
            .GroupBy(v => v.Instance.Severity)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<(AlertInstance? Instance, IReadOnlyList<AlertTransition> Transitions, IReadOnlyList<TriggerEventView> Events)> GetInstanceAsync(
        long instanceId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        AlertInstance? instance = await _instances.GetAsync(instanceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return (null, [], []);
        }

        AlertRow? alert = await _alerts.GetAsync(instance.AlertId, ct).ConfigureAwait(false);
        if (alert is not null && !ScopeAllows(alert, user.Scope))
        {
            return (null, [], []);
        }

        IReadOnlyList<AlertTransition> transitions = await _instances.ListTransitionsAsync(instanceId, ct).ConfigureAwait(false);

        var events = new List<TriggerEventView>();
        foreach (long eventId in instance.TriggerEventIds)
        {
            SyslogEvent? evt = await _events.GetByIdAsync(user.Scope, eventId, ct).ConfigureAwait(false);
            events.Add(evt is null
                ? new TriggerEventView(eventId, DateTimeOffset.MinValue, null, string.Empty, Visible: false)
                : new TriggerEventView(eventId, evt.ReceivedUtc, evt.Hostname, evt.Message, Visible: true));
        }

        return (instance, transitions, events);
    }

    // ---------------------------------------------------------------- promote / one-click

    public async Task<(AlertDefinition Draft, IReadOnlyList<string> Notes)> PromoteFromSavedSearchAsync(long savedSearchId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        SavedSearch? saved = await _savedSearches.GetAsync(savedSearchId, await CurrentUserIdAsync().ConfigureAwait(false), ct).ConfigureAwait(false);

        var draft = new AlertDefinition
        {
            Name = saved is null ? "New alert from search" : $"Alert: {saved.Name}",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 300,
            IntervalSeconds = 60,
            Threshold = 5,
            Actions = [new Core.Rules.RaiseNotificationAction { Title = "{hostname}: alert" }],
        };

        var notes = new List<string>();
        if (saved is null)
        {
            notes.Add("The saved search could not be loaded — start from a blank alert.");
            return (draft, notes);
        }

        SearchParseResult parsed = SearchQueryParser.Parse(saved.QueryText);
        if (parsed.Success)
        {
            SearchConditionTranslation translation = SearchToConditionTranslator.Translate(parsed.Query);
            draft.Filter = translation.Filter;
            notes.AddRange(translation.Unsupported);
        }
        else
        {
            notes.Add($"The search query could not be parsed ({parsed.Error}) — set the filter manually.");
        }

        _ = user;
        return (draft, notes);
    }

    public async Task<AlertActionResult> CreateDeviceSilentAsync(long deviceId, CancellationToken ct)
    {
        if (!await CanEditAsync().ConfigureAwait(false))
        {
            return AlertActionResult.Fail("You do not have permission to add alerts.");
        }

        VSoftSol.Syslog.Core.Devices.Device? device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            return AlertActionResult.Fail("That device does not exist.");
        }

        IReadOnlyList<AlertRow> existing = await _alerts.ListAllAsync(ct).ConfigureAwait(false);
        AlertRow? estateWide = existing.FirstOrDefault(a => a.Type == AlertEvaluationType.DeviceSilent && a.DeviceGroupIds.Count == 0);
        if (estateWide is not null)
        {
            if (!estateWide.Enabled)
            {
                await SetEnabledAsync(estateWide.AlertId, true, ct).ConfigureAwait(false);
            }

            return AlertActionResult.Success($"'{estateWide.Name}' already watches every monitored device — {device.Name} is covered. Set its heartbeat on the device page to tune when it fires.");
        }

        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        var alert = new AlertDefinition
        {
            Name = "A monitored device goes silent",
            Description = $"Created from the {device.Name} health card. Watches every device; each device's own heartbeat threshold applies.",
            Type = AlertEvaluationType.DeviceSilent,
            Severity = NotificationLevel.Critical,
            Enabled = true,
            WindowSeconds = Math.Max(300, (device.HeartbeatMinutes ?? 15) * 60),
            IntervalSeconds = 300,
            AutoResolve = true,
            Actions = [new Core.Rules.RaiseNotificationAction { Level = NotificationLevel.Critical, Title = "Device silent: {hostname}", Body = "No logs received for longer than its heartbeat threshold." }],
        };

        long id = await _alerts.CreateAsync(alert, user.UserName, ct).ConfigureAwait(false);
        await Audit(AuditActions.AlertCreate, id, alert.Name, user).ConfigureAwait(false);
        return AlertActionResult.Success($"Created '{alert.Name}'. It watches every monitored device — tune {device.Name}'s heartbeat on its device page.");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IReadOnlyList<AlertInstanceView>> JoinAndScopeAsync(
        IReadOnlyList<AlertInstance> instances, UserScope scope, CancellationToken ct)
    {
        if (instances.Count == 0)
        {
            return [];
        }

        IReadOnlyList<AlertRow> defs = await _alerts.ListAllAsync(ct).ConfigureAwait(false);
        Dictionary<long, AlertRow> byId = defs.ToDictionary(d => d.AlertId);

        var views = new List<AlertInstanceView>(instances.Count);
        foreach (AlertInstance instance in instances)
        {
            if (!byId.TryGetValue(instance.AlertId, out AlertRow? alert))
            {
                continue;
            }

            if (!ScopeAllows(alert, scope))
            {
                continue;
            }

            views.Add(new AlertInstanceView(instance, alert.Name, alert.Type));
        }

        return views;
    }

    private static bool ScopeAllows(AlertRow alert, UserScope scope)
    {
        if (scope.IsUnrestricted)
        {
            return true;
        }

        // An unrestricted alert (watches everything) is a system-wide signal; its existence
        // is not sensitive and its triggering events are still scope-checked on drill-down.
        if (alert.StreamIds.Count == 0 && alert.DeviceGroupIds.Count == 0)
        {
            return true;
        }

        bool streamsOk = alert.StreamIds.Count == 0 || (!scope.AllStreams && alert.StreamIds.All(scope.StreamIds.Contains));
        bool groupsOk = alert.DeviceGroupIds.Count == 0 || (!scope.AllDeviceGroups && alert.DeviceGroupIds.Any(scope.DeviceGroupIds.Contains));
        return streamsOk && groupsOk;
    }

    private async Task<bool> CanEditAsync()
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        return user.Role is Role.Administrator or Role.Operator;
    }

    private async Task<long> CurrentUserIdAsync()
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (!user.IsAuthenticated)
        {
            return 0;
        }

        VSoftSol.Syslog.Data.Users.UserAccount? account =
            await _userStore.FindByUsernameAsync(user.UserName, CancellationToken.None).ConfigureAwait(false);
        return account?.UserId ?? 0;
    }

    private async Task<string> UniqueNameAsync(string baseName, CancellationToken ct)
    {
        IReadOnlyList<AlertRow> existing = await _alerts.ListAllAsync(ct).ConfigureAwait(false);
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

    private Task Audit(string action, long alertId, string name, CurrentUser user) =>
        _audit.AppendAsync(
            new AuditEntry(action, user.UserName, "alert",
                alertId.ToString(System.Globalization.CultureInfo.InvariantCulture), Detail: name),
            CancellationToken.None);
}
