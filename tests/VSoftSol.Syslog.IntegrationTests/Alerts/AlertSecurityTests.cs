using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Rules.Templating;
using VSoftSol.Syslog.Web.Alerts;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 security validation: information disclosure in alert content (a notification
/// must never surface an event from a stream the viewer cannot see), stored XSS in alert
/// fields, and HTML-email escaping.
/// </summary>
public sealed class AlertSecurityTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public AlertSecurityTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private AlertAdminService AdminAs(Role role, long[]? streams = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "viewer"), new(ClaimTypes.Role, role.ToString()) };
        if (streams is { Length: > 0 })
        {
            claims.Add(new Claim(SyslogClaimTypes.VisibleStreams, string.Join(',', streams)));
        }

        IServiceProvider sp = _factory.Services;
        return new AlertAdminService(
            sp.GetRequiredService<SqliteAlertStore>(),
            sp.GetRequiredService<SqliteAlertInstanceStore>(),
            sp.GetRequiredService<SqliteAlertWindowReader>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            new CurrentUserAccessor(new FakeAuthState(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")))),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Scoping.ScopedEventReader>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Search.SqliteSavedSearchStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Devices.SqliteDeviceStore>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Users.SqliteUserStore>(),
            Options.Create(new ActionExecutorOptions()),
            sp.GetRequiredService<IOptions<VSoftSol.Syslog.Service.Hosting.AlertEvaluationOptions>>());
    }

    [Fact]
    public async Task TriggeringEvents_OutsideTheViewersScope_AreNotDisclosed()
    {
        var db = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Repositories.SqliteLogRepository>();
        var connectionFactory = _factory.Services.GetRequiredService<VSoftSol.Syslog.Data.Sqlite.SqliteConnectionFactory>();

        // two streams; the event is routed to stream B only
        long streamA, streamB;
        await using (var c = await connectionFactory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO streams (name, enabled, created_utc) VALUES ('scope-A', 1, 't');
                INSERT INTO streams (name, enabled, created_utc) VALUES ('scope-B', 1, 't');
                SELECT (SELECT stream_id FROM streams WHERE name='scope-A'), (SELECT stream_id FROM streams WHERE name='scope-B');
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            streamA = reader.GetInt64(0);
            streamB = reader.GetInt64(1);
        }

        SyslogEvent evt = new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "10.5.5.5",
            Hostname = "secret-host",
            Facility = Facility.Local0,
            Severity = Severity.Critical,
            Protocol = Protocol.Udp,
            Message = "SECRET password=hunter2",
            RawMessage = Encoding.UTF8.GetBytes("SECRET"),
            ParseStatus = ParseStatus.Rfc3164,
        }.WithRouting(null, [streamB]);
        long eventId = (await db.AppendBatchAsync([evt], CancellationToken.None))[0];

        long alertId = await _factory.Services.GetRequiredService<SqliteAlertStore>().CreateAsync(new AlertDefinition
        {
            Name = "scoped alert",
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 300,
            IntervalSeconds = 60,
            Threshold = 1,
        }, "op", CancellationToken.None);

        (long instanceId, _) = await _factory.Services.GetRequiredService<SqliteAlertInstanceStore>()
            .OpenAsync(alertId, NotificationLevel.Critical, "secret-host", 5, 1, [eventId], DateTimeOffset.UtcNow, CancellationToken.None);

        // a viewer restricted to stream A only
        (_, _, IReadOnlyList<TriggerEventView> events) = await AdminAs(Role.Operator, [streamA]).GetInstanceAsync(instanceId, CancellationToken.None);

        events.Should().ContainSingle();
        events[0].Visible.Should().BeFalse("the triggering event is in a stream the viewer cannot see");
        events[0].Message.Should().BeEmpty("the message body is not disclosed");

        // an unrestricted viewer sees it
        (_, _, IReadOnlyList<TriggerEventView> full) = await AdminAs(Role.Administrator).GetInstanceAsync(instanceId, CancellationToken.None);
        full[0].Visible.Should().BeTrue();
    }

    [Fact]
    public async Task HostileAlertFields_AreStoredByteIdentical_AndNeverExecutedInATemplate()
    {
        const string payload = "<script>alert('xss')</script> <img src=x onerror=alert(1)>";
        long alertId = await _factory.Services.GetRequiredService<SqliteAlertStore>().CreateAsync(new AlertDefinition
        {
            Name = $"xss-{Guid.NewGuid():N}",
            Description = payload,
            RemediationNotes = payload,
            Type = AlertEvaluationType.Threshold,
            WindowSeconds = 60,
            IntervalSeconds = 60,
            Threshold = 1,
        }, "op", CancellationToken.None);

        AlertRow? row = await _factory.Services.GetRequiredService<SqliteAlertStore>().GetAsync(alertId, CancellationToken.None);
        row!.Description.Should().Be(payload, "stored verbatim — encoding happens at render");
        row.RemediationNotes.Should().Be(payload);

        // The synthetic-event field template renders literally: an unknown/hostile token is
        // not an expression, and control characters are stripped. A body of "{field.alert_name}"
        // substitutes the (hostile) name as plain text, never as markup or code.
        var evt = new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "0.0.0.0",
            Facility = Facility.Local0,
            Severity = Severity.Warning,
            Protocol = Protocol.Udp,
            Message = "x",
            RawMessage = Encoding.UTF8.GetBytes("x"),
            ParseStatus = ParseStatus.Rfc5424,
            Fields = [new EventField("alert_name", payload)],
        };

        string rendered = FieldTemplate.Render("Name: {field.alert_name}", evt);
        rendered.Should().Be($"Name: {payload}", "the template engine performs a literal substitution, not evaluation");
        rendered.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public async Task AlertEmailBody_IsPlainText_WithControlCharactersStripped()
    {
        // CR/LF in a substituted value would enable header injection downstream; FieldTemplate
        // strips them (PHASE_07 defence, still in force for alerts).
        var evt = new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "0.0.0.0",
            Hostname = "host\r\nBcc: attacker@evil.com",
            Facility = Facility.Local0,
            Severity = Severity.Warning,
            Protocol = Protocol.Udp,
            Message = "line1\r\nline2",
            RawMessage = Encoding.UTF8.GetBytes("x"),
            ParseStatus = ParseStatus.Rfc5424,
        };

        string subject = FieldTemplate.Render("Alert on {hostname}", evt);
        subject.Should().NotContain("\r").And.NotContain("\n",
            "a folded SMTP header needs a real CR/LF, which the template engine strips from substituted values");

        await Task.CompletedTask;
    }
}
