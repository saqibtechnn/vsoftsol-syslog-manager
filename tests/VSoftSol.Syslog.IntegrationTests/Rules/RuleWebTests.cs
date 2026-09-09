using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Rules;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 web surface: route authorization, role enforced <b>at the service</b> (not just
/// hidden in the UI), the dry-run executes nothing, and a template clone starts disabled.
/// </summary>
public sealed class RuleWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public RuleWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static ClaimsPrincipal Principal(Role role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "t"), new Claim(ClaimTypes.Role, role.ToString())], "Test"));

    private RuleAdminService AdminAs(Role role)
    {
        IServiceProvider sp = _factory.Services;
        return new RuleAdminService(
            sp.GetRequiredService<SqliteRuleStore>(),
            sp.GetRequiredService<RuleSetProvider>(),
            sp.GetRequiredService<ActionExecutorRegistry>(),
            sp.GetRequiredService<VSoftSol.Syslog.Data.Audit.SqliteAuditLog>(),
            new CurrentUserAccessor(new FakeAuthState(Principal(role))),
            (_, _) => new ValueTask<string?>((string?)null),
            (_, _, _, _, _) => ValueTask.CompletedTask,
            Options.Create(new ActionExecutorOptions()));
    }

    private static SyslogEvent Sample(string message = "critical: power supply failure", Severity sev = Severity.Critical) => new()
    {
        ReceivedUtc = DateTimeOffset.UtcNow,
        SourceIp = "203.0.113.5",
        Hostname = "core-sw-1",
        Severity = sev,
        Facility = Facility.Local0,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc3164,
    };

    [Theory]
    [InlineData("/rules")]
    [InlineData("/rules/0")]
    [InlineData("/rules/templates")]
    [InlineData("/rules/tester")]
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
        RuleActionResult result = await AdminAs(Role.ReadOnly).SaveAsync(new RuleDefinition
        {
            Name = "ro attempt",
            Actions = [new RaiseNotificationAction { Title = "x" }],
        }, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("permission");
    }

    [Fact]
    public async Task Delete_IsAdministratorOnly()
    {
        long id = await AdminAs(Role.Administrator).SaveAsync(new RuleDefinition
        {
            Name = "to-delete",
            Actions = [new RaiseNotificationAction { Title = "x" }],
        }, CancellationToken.None) is { Ok: true }
            ? (await _factory.Services.GetRequiredService<SqliteRuleStore>().ListAllAsync(CancellationToken.None))
                .First(r => r.Name == "to-delete").RuleId
            : 0;

        (await AdminAs(Role.Operator).DeleteAsync(id, CancellationToken.None)).Ok.Should().BeFalse();
        (await AdminAs(Role.Administrator).DeleteAsync(id, CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task DryRun_ExecutesNothing_JustReportsWouldFire()
    {
        RuleAdminService admin = AdminAs(Role.Administrator);
        await admin.SaveAsync(new RuleDefinition
        {
            Name = "dry-run me",
            Filter = new Core.Conditions.ConditionGroup
            {
                Join = Core.Conditions.ConditionJoin.Or,
                Children = { new Core.Conditions.ConditionComparison { Field = "message", Operator = Core.Conditions.ConditionOperator.Contains, Value = "power supply" } },
            },
            Actions = [new HttpWebhookAction { Url = "http://198.51.100.9/hook" }],
        }, CancellationToken.None);

        IReadOnlyList<RuleDryRun> results = await admin.DryRunAsync(Sample(), CancellationToken.None);
        RuleDryRun mine = results.Single(r => r.RuleName == "dry-run me");

        mine.Matched.Should().BeTrue();
        mine.WouldRun.Should().ContainSingle().Which.Should().Be("HTTP webhook");
        // nothing executed → no audit 'action.fired' and no outbox rows
        (await CountAsync("SELECT COUNT(*) FROM rule_action_queue;")).Should().Be(0);
    }

    [Fact]
    public async Task CloneTemplate_CreatesADisabledRule()
    {
        RuleActionResult result = await AdminAs(Role.Operator).CloneTemplateAsync(1, CancellationToken.None);
        result.Ok.Should().BeTrue();

        IReadOnlyList<RuleRow> rules = await _factory.Services.GetRequiredService<SqliteRuleStore>().ListAllAsync(CancellationToken.None);
        rules.Should().Contain(r => r.Name == RuleAdminService.Templates[1].Rule.Name && !r.Enabled);
    }

    [Fact]
    public async Task TestAction_WithReallyExecute_RunsTheExecutor_AndAudits()
    {
        var action = new HttpWebhookAction { Url = "http://198.51.100.9/hook", TimeoutSeconds = 1 };
        RuleActionResult result = await AdminAs(Role.Administrator)
            .TestActionAsync(action, Sample(), reallyExecute: true, CancellationToken.None);

        result.Ok.Should().BeFalse("the black-hole target fails");
        (await CountAsync($"SELECT COUNT(*) FROM audit_log WHERE action = 'action.tested';")).Should().BeGreaterThan(0);
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
