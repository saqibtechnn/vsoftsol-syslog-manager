using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Data.Streams;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;
using VSoftSol.Syslog.Web.Streams;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Devices;

/// <summary>
/// PHASE_06 Slice D security: a stream-scoped user sees and can act on only their streams
/// (no IDOR, no existence oracle) in the admin service that backs every stream picker, and
/// hostile device fields from the wire render encoded on the device detail health card.
/// </summary>
public sealed class StreamScopeAndXssTests
{
    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static ClaimsPrincipal Principal(Role role, params long[] visibleStreamIds)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "scoped-op"),
            new(ClaimTypes.Role, role.ToString()),
        };
        if (visibleStreamIds.Length > 0)
        {
            claims.Add(new Claim(SyslogClaimTypes.VisibleStreams, string.Join(",", visibleStreamIds)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static StreamAdminService AdminFor(SqliteTestDatabase db, ClaimsPrincipal principal)
    {
        var streamStore = new SqliteStreamStore(db.Factory);
        var routerProvider = new StreamRouterProvider(streamStore, NullLogger<StreamRouterProvider>.Instance);
        var audit = new SqliteAuditLog(db.Factory);
        return new StreamAdminService(
            streamStore, routerProvider, audit, new CurrentUserAccessor(new FakeAuthState(principal)));
    }

    private static ConditionGroup Rule(string needle) => new()
    {
        Join = ConditionJoin.Or,
        Children = { new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = needle } },
    };

    [Fact]
    public async Task ListAsync_ForAStreamScopedUser_ReturnsOnlyTheirStreams()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteStreamStore(db.Factory);
        long mine = await store.CreateAsync("Mine", null, Rule("mine"), "admin", CancellationToken.None);
        long theirs = await store.CreateAsync("Theirs", null, Rule("theirs"), "admin", CancellationToken.None);

        StreamAdminService scoped = AdminFor(db, Principal(Role.Operator, mine));
        IReadOnlyList<StreamRow> visible = await scoped.ListAsync(CancellationToken.None);

        visible.Select(s => s.StreamId).Should().Contain(mine);
        visible.Select(s => s.StreamId).Should().NotContain(theirs);
        visible.Select(s => s.Name).Should().NotContain("All Messages"); // a seeded stream not in scope
    }

    [Fact]
    public async Task GetAsync_ForAnOutOfScopeStreamId_ReturnsNull_NoExistenceOracle()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteStreamStore(db.Factory);
        long mine = await store.CreateAsync("Mine", null, Rule("mine"), "admin", CancellationToken.None);
        long theirs = await store.CreateAsync("Theirs", null, Rule("theirs"), "admin", CancellationToken.None);

        StreamAdminService scoped = AdminFor(db, Principal(Role.Operator, mine));

        (await scoped.GetAsync(theirs, CancellationToken.None)).Should().BeNull("out of scope");
        (await scoped.GetAsync(999_999, CancellationToken.None)).Should().BeNull("does not exist — same answer");
        (await scoped.GetAsync(mine, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_CannotEditAnOutOfScopeStream()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteStreamStore(db.Factory);
        long mine = await store.CreateAsync("Mine", null, Rule("mine"), "admin", CancellationToken.None);
        long theirs = await store.CreateAsync("Theirs", null, Rule("theirs"), "admin", CancellationToken.None);

        StreamAdminService scoped = AdminFor(db, Principal(Role.Operator, mine));
        StreamActionResult result = await scoped.SaveAsync(
            theirs, "Hijacked", null, enabled: true, Rule("x"), CancellationToken.None);

        result.Ok.Should().BeFalse();
        StreamRow? untouched = await store.GetAsync(theirs, CancellationToken.None);
        untouched!.Name.Should().Be("Theirs");
    }

    [Fact]
    public async Task DeviceDetailHealthCard_RendersHostileDeviceFieldsEncoded()
    {
        using var factory = new SyslogWebApplicationFactory();
        const string payload = "<script>alert(1)</script>";

        long deviceId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<SqliteDeviceStore>();
            deviceId = await store.CreateAsync(
                new Device
                {
                    DeviceId = 0,
                    Name = payload,
                    Hostname = payload,
                    Vendor = payload,
                    PrimaryIp = "203.0.113.50",
                    ApprovalStatus = DeviceApprovalStatus.Approved,
                },
                CancellationToken.None);
        }

        var admin = new WebAuthClient(factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        string html = await admin.Client.GetStringAsync($"/devices/{deviceId}");
        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().Contain("alert(1)"); // the text is shown, HTML-encoded
    }
}
