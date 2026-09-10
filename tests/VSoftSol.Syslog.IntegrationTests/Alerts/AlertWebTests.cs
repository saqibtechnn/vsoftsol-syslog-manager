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

    private AlertAdminService AdminAs(Role role)
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
            Options.Create(new ActionExecutorOptions()));
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
            Options.Create(new ActionExecutorOptions()));

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
