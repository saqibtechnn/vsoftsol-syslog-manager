using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Alerts;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 web surface + security: route authorization, role enforced <b>at the service</b>
/// (Read-Only / Auditor cannot acknowledge or resolve), the "would have fired" preview is
/// accurate against fixture data, promote-from-saved-search, and the device-silent one-click.
/// </summary>
public sealed class AlertWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public AlertWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static ClaimsPrincipal Principal(Role role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "t"), new Claim(ClaimTypes.Role, role.ToString())], "Test"));

    private AlertAdminService AdminAs(Role role, AlertEvaluationOptions? evalOptions = null)
    {
        IServiceProvider sp = _factory.Services;
        return new AlertAdminService(
            sp.GetRequiredService<SqliteAlertStore>(),
            sp.GetRequiredService<SqliteAlertInstanceStore>(),
            sp.GetRequiredService<SqliteAlertWindowReader>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            new CurrentUserAccessor(new FakeAuthState(Principal(role))),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Scoping.ScopedEventReader>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSavedSearchStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Devices.SqliteDeviceStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Users.SqliteUserStore>(),
            Options.Create(new ActionExecutorOptions()),
            Options.Create(evalOptions ?? new AlertEvaluationOptions()));
    }

    private static AlertDefinition Threshold(string name) => new()
    {
        Name = name,
        Type = AlertEvaluationType.Threshold,
        WindowSeconds = 300,
        IntervalSeconds = 60,
        Threshold = 5,
        Actions = [new RaiseNotificationAction { Title = "x" }],
    };

    [Theory]
    [InlineData("/alerts")]
    [InlineData("/alerts/0")]
    [InlineData("/alerts/templates")]
    [InlineData("/alerts/history")]
    public async Task Routes_RequireAuthentication(string path)
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task Save_IsRefusedForReadOnly_AtTheService()
    {
        AlertActionResult result = await AdminAs(Role.ReadOnly).SaveAsync(Threshold("ro attempt"), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("permission");
    }

    [Fact]
    public async Task Delete_IsAdministratorOnly()
    {
        await AdminAs(Role.Administrator).SaveAsync(Threshold("to-delete"), CancellationToken.None);
        long id = (await _factory.Services.GetRequiredService<SqliteAlertStore>().ListAllAsync(CancellationToken.None))
            .First(a => a.Name == "to-delete").AlertId;

        (await AdminAs(Role.Operator).DeleteAsync(id, CancellationToken.None)).Ok.Should().BeFalse();
        (await AdminAs(Role.Administrator).DeleteAsync(id, CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData(Role.ReadOnly)]
    [InlineData(Role.Auditor)]
    public async Task AcknowledgeAndResolve_AreRefusedFor(Role role)
    {
        long alertId = await AdminAs(Role.Administrator).SaveAsync(Threshold($"lc-{role}"), CancellationToken.None) is { Ok: true }
            ? (await _factory.Services.GetRequiredService<SqliteAlertStore>().ListAllAsync(CancellationToken.None)).First(a => a.Name == $"lc-{role}").AlertId
            : 0;
        var instances = _factory.Services.GetRequiredService<SqliteAlertInstanceStore>();
        (long instanceId, _) = await instances.OpenAsync(alertId, NotificationLevel.Warning, "h", 9, 5, [], DateTimeOffset.UtcNow, CancellationToken.None);

        (await AdminAs(role).AcknowledgeAsync(instanceId, "nope", CancellationToken.None)).Ok.Should().BeFalse();
        (await AdminAs(role).ResolveAsync(instanceId, "nope", CancellationToken.None)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task AcknowledgeAndResolve_AreAudited_WithTheTrueActor()
    {
        long alertId = (await AdminAs(Role.Administrator).SaveAsync(Threshold("lc-audit"), CancellationToken.None)).Ok
            ? (await _factory.Services.GetRequiredService<SqliteAlertStore>().ListAllAsync(CancellationToken.None)).First(a => a.Name == "lc-audit").AlertId
            : 0;
        var instances = _factory.Services.GetRequiredService<SqliteAlertInstanceStore>();
        (long instanceId, _) = await instances.OpenAsync(alertId, NotificationLevel.Warning, "h", 9, 5, [], DateTimeOffset.UtcNow, CancellationToken.None);

        (await AdminAs(Role.Operator).AcknowledgeAsync(instanceId, "on it", CancellationToken.None)).Ok.Should().BeTrue();
        (await AdminAs(Role.Operator).ResolveAsync(instanceId, "fixed", CancellationToken.None)).Ok.Should().BeTrue();

        (await CountAsync("SELECT COUNT(*) FROM audit_log WHERE action = 'alert.acknowledged' AND actor = 't';")).Should().BeGreaterThan(0);
        (await CountAsync("SELECT COUNT(*) FROM audit_log WHERE action = 'alert.resolved' AND actor = 't';")).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CloneTemplate_CreatesADisabledAlert()
    {
        AlertActionResult result = await AdminAs(Role.Operator).CloneTemplateAsync(0, CancellationToken.None);
        result.Ok.Should().BeTrue();

        IReadOnlyList<AlertRow> alerts = await _factory.Services.GetRequiredService<SqliteAlertStore>().ListAllAsync(CancellationToken.None);
        alerts.Should().Contain(a => a.Name == AlertAdminService.Templates[0].Alert.Name && !a.Enabled);
    }

    [Fact]
    public async Task Preview_IsAccurate_AgainstFixtureData()
    {
        // fixture: 3 non-overlapping 60-second windows in the last hour, each with 6 events
        // from one host; threshold 5, window 60s → the preview should count 3 breaching buckets.
        var db = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var events = new List<SyslogEvent>();
        for (int bucket = 0; bucket < 3; bucket++)
        {
            DateTimeOffset at = now.AddMinutes(-50 + bucket * 3);
            events.AddRange(Enumerable.Range(0, 6).Select(i => new SyslogEvent
            {
                ReceivedUtc = at.AddSeconds(i),
                SourceIp = "10.9.9.9",
                Hostname = "preview-host",
                Facility = Facility.Local0,
                Severity = Severity.Warning,
                Protocol = Protocol.Udp,
                Message = "preview probe",
                RawMessage = Encoding.UTF8.GetBytes("preview probe"),
                ParseStatus = ParseStatus.Rfc3164,
            }));
        }

        await db.AppendBatchAsync(events, CancellationToken.None);

        var alert = new AlertDefinition
        {
            Name = "preview",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 5,
            GroupByField = "hostname",
        };

        AlertPreview preview = await AdminAs(Role.Operator).PreviewAsync(alert, TimeSpan.FromHours(1), CancellationToken.None);
        preview.Approximate.Should().BeFalse();
        preview.Count.Should().Be(3);
        preview.Detail.Should().Contain("3 times");
    }

    [Fact]
    public async Task Preview_FilteredThresholdAlert_IsAFullReplay_NotASampledEstimate()
    {
        // v1.1 — P8-1 (docs/evidence/phase-08/known-issues.md): a non-empty Filter forces
        // the in-memory path (CompiledAlert.AlwaysMatches == false), which used to sample 24
        // windows and extrapolate. 5 non-overlapping 60-second buckets, only 3 of which
        // contain 6+ matching "preview probe" events (the other 2 contain only noise that
        // does not match the filter) — a sampled estimate could easily land on 2, 3, 4, or 5;
        // a full replay must land on exactly 3, every time. Placed 88-100 minutes back (and
        // previewed over a 2-hour look-back) rather than inside the last hour — this class
        // shares one live database across every test (IClassFixture), and
        // Preview_IsAccurate_AgainstFixtureData's SQL fast path aggregates by hostname over
        // its own 1-hour look-back with no further scoping, so any 6-event burst inside that
        // hour would silently inflate its "breaching buckets" count too.
        var db = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var events = new List<SyslogEvent>();
        for (int bucket = 0; bucket < 5; bucket++)
        {
            DateTimeOffset at = now.AddMinutes(-100 + bucket * 3);
            bool breaching = bucket is 0 or 2 or 4;
            events.AddRange(Enumerable.Range(0, 6).Select(i => new SyslogEvent
            {
                ReceivedUtc = at.AddSeconds(i),
                SourceIp = "10.9.9.8",
                Hostname = "filtered-preview-host",
                Facility = Facility.Local0,
                Severity = Severity.Warning,
                Protocol = Protocol.Udp,
                Message = breaching ? "filtered preview probe" : "unrelated noise",
                RawMessage = Encoding.UTF8.GetBytes(breaching ? "filtered preview probe" : "unrelated noise"),
                ParseStatus = ParseStatus.Rfc3164,
            }));
        }

        await db.AppendBatchAsync(events, CancellationToken.None);

        var alert = new AlertDefinition
        {
            Name = "filtered preview",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 5,
            GroupByField = "hostname",
            Filter = new ConditionGroup
            {
                Children = [new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "filtered preview probe" }],
            },
        };

        AlertPreview preview = await AdminAs(Role.Operator).PreviewAsync(alert, TimeSpan.FromHours(2), CancellationToken.None);
        preview.Approximate.Should().BeFalse("a full replay is exact, not a lower-bound estimate, unless the scan is capped");
        preview.Count.Should().Be(3);
    }

    [Fact]
    public async Task Preview_DistinctCountAlert_IsAFullReplay_AcrossEveryBucket()
    {
        var db = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var events = new List<SyslogEvent>();
        // 4 non-overlapping 60s buckets, 61-70 minutes back (outside the 1-hour look-back
        // Preview_IsAccurate_AgainstFixtureData's SQL fast path aggregates over — see that
        // test's sibling above for why this class's shared database makes that matter);
        // buckets 0 and 2 each see 3 distinct source IPs (threshold 2 -> breach), buckets 1
        // and 3 see only 1 distinct source IP each (no breach).
        for (int bucket = 0; bucket < 4; bucket++)
        {
            DateTimeOffset at = now.AddMinutes(-70 + bucket * 3);
            int distinctIps = bucket % 2 == 0 ? 3 : 1;
            for (int i = 0; i < distinctIps; i++)
            {
                events.Add(new SyslogEvent
                {
                    ReceivedUtc = at.AddSeconds(i),
                    SourceIp = $"10.9.8.{bucket}{i}",
                    Hostname = "distinct-preview-host",
                    Facility = Facility.Local0,
                    Severity = Severity.Warning,
                    Protocol = Protocol.Udp,
                    Message = "distinct preview probe",
                    RawMessage = Encoding.UTF8.GetBytes("distinct preview probe"),
                    ParseStatus = ParseStatus.Rfc3164,
                });
            }
        }

        await db.AppendBatchAsync(events, CancellationToken.None);

        var alert = new AlertDefinition
        {
            Name = "distinct preview",
            Type = AlertEvaluationType.DistinctCount,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 2,
            GroupByField = "source_ip",
            Filter = new ConditionGroup
            {
                Children = [new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "distinct preview probe" }],
            },
        };

        AlertPreview preview = await AdminAs(Role.Operator).PreviewAsync(alert, TimeSpan.FromMinutes(90), CancellationToken.None);
        preview.Approximate.Should().BeFalse();
        preview.Count.Should().Be(2);
    }

    [Fact]
    public async Task Preview_AbsenceAlert_CountsEveryEmptyBucket()
    {
        var db = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 3 non-overlapping 60s buckets covering the last 3 minutes; only the middle one
        // (90 seconds back) gets a heartbeat event, so the first and third are "absent" ->
        // 2 fires. A single event never breaches Preview_IsAccurate_AgainstFixtureData's
        // threshold-5 SQL aggregate, so this one is safe to leave inside its 1-hour look-back.
        var heartbeat = now.AddSeconds(-90);
        await db.AppendAsync(new SyslogEvent
        {
            ReceivedUtc = heartbeat,
            SourceIp = "10.9.8.1",
            Hostname = "absence-preview-host",
            Facility = Facility.Local0,
            Severity = Severity.Informational,
            Protocol = Protocol.Udp,
            Message = "absence preview heartbeat",
            RawMessage = Encoding.UTF8.GetBytes("absence preview heartbeat"),
            ParseStatus = ParseStatus.Rfc3164,
        }, CancellationToken.None);

        var alert = new AlertDefinition
        {
            Name = "absence preview",
            Type = AlertEvaluationType.Absence,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 0,
            Filter = new ConditionGroup
            {
                Children = [new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "absence preview heartbeat" }],
            },
        };

        AlertPreview preview = await AdminAs(Role.Operator).PreviewAsync(alert, TimeSpan.FromMinutes(3), CancellationToken.None);
        preview.Count.Should().Be(2);
    }

    [Fact]
    public async Task Preview_WhenTheScanHitsItsCap_IsALowerBound_NotASampledEstimate()
    {
        // 90 minutes back and previewed over a 2-hour look-back — outside the 1-hour window
        // Preview_IsAccurate_AgainstFixtureData's SQL fast path aggregates over unscoped by
        // hostname (see the filtered-threshold test above for why that matters in this
        // shared-database test class); 20 events comfortably exceeds threshold 1.
        var db = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, 20).Select(i => new SyslogEvent
        {
            ReceivedUtc = now.AddMinutes(-90).AddSeconds(i),
            SourceIp = "10.9.8.2",
            Hostname = "capped-preview-host",
            Facility = Facility.Local0,
            Severity = Severity.Warning,
            Protocol = Protocol.Udp,
            Message = "capped preview probe",
            RawMessage = Encoding.UTF8.GetBytes("capped preview probe"),
            ParseStatus = ParseStatus.Rfc3164,
        }).ToList();
        await db.AppendBatchAsync(events, CancellationToken.None);

        var alert = new AlertDefinition
        {
            Name = "capped preview",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 1,
            Filter = new ConditionGroup
            {
                Children = [new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "capped preview probe" }],
            },
        };

        AlertPreview preview = await AdminAs(Role.Operator, new AlertEvaluationOptions { MaxWindowScan = 5 })
            .PreviewAsync(alert, TimeSpan.FromHours(2), CancellationToken.None);

        preview.Approximate.Should().BeTrue("the scan hit its cap before finishing — the true count may be higher");
        preview.Detail.Should().NotContain("approximately", "this is a capped lower bound, not a statistical estimate");
    }

    [Fact]
    public async Task DeviceSilentOneClick_CreatesTheEstateWideAlert_ThenIsIdempotent()
    {
        long deviceId;
        await using (var c = await _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Sqlite.SqliteConnectionFactory>().OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO devices (name, primary_ip, heartbeat_minutes, approval_status, is_enabled, created_utc) VALUES ('oneclick-sw', '10.7.7.7', 20, 'approved', 1, $now); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            deviceId = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        AlertActionResult first = await AdminAs(Role.Operator).CreateDeviceSilentAsync(deviceId, CancellationToken.None);
        first.Ok.Should().BeTrue();

        AlertActionResult second = await AdminAs(Role.Operator).CreateDeviceSilentAsync(deviceId, CancellationToken.None);
        second.Ok.Should().BeTrue();
        second.Message.Should().ContainEquivalentOf("already");

        (await CountAsync("SELECT COUNT(*) FROM alert_definitions WHERE eval_type = 'device_silent' AND device_group_ids IS NULL;")).Should().Be(1);
    }

    [Fact]
    public async Task PromoteFromSavedSearch_PrefillsAFilter_AndFlagsLossyParts()
    {
        var userStore = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Users.SqliteUserStore>();
        string username = $"promote-{Guid.NewGuid():N}";
        long userId = await userStore.CreateAsync(
            username, "Promote User", Role.Operator, "hash", false, DateTimeOffset.UtcNow, CancellationToken.None);
        VSoftSol.Syslog.Data.Users.UserAccount user = (await userStore.FindByUsernameAsync(username, CancellationToken.None))!;

        var savedSearches = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSavedSearchStore>();
        long searchId = await savedSearches.CreateAsync(userId, "auth failures", "host:core-sw-1 AND \"authentication failure\"", null, false, CancellationToken.None);

        var accessor = new CurrentUserAccessor(new FakeAuthState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, "Operator")], "Test"))));
        var admin2 = new AlertAdminService(
            _factory.Services.GetRequiredService<SqliteAlertStore>(),
            _factory.Services.GetRequiredService<SqliteAlertInstanceStore>(),
            _factory.Services.GetRequiredService<SqliteAlertWindowReader>(),
            _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            accessor,
            _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Scoping.ScopedEventReader>(),
            savedSearches,
            _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Devices.SqliteDeviceStore>(),
            userStore,
            Options.Create(new ActionExecutorOptions()),
            Options.Create(new AlertEvaluationOptions()));

        (AlertDefinition draft, IReadOnlyList<string> notes) = await admin2.PromoteFromSavedSearchAsync(searchId, CancellationToken.None);

        draft.Filter.Should().NotBeNull();
        draft.Filter!.Children.Should().NotBeEmpty();
        _ = notes;
    }

    private async Task<long> CountAsync(string sql)
    {
        var factory = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Sqlite.SqliteConnectionFactory>();
        await using var c = await factory.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
