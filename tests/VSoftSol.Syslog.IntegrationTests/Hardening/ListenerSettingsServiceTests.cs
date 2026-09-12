using System.Runtime.Versioning;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Hardening;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

/// <summary>
/// v1.1 — Settings → Listeners gains a UDP/TCP "core syslog" card
/// (<see cref="ListenerSettingsService.SetUdpTcpPortsAsync"/>) alongside the existing
/// TLS/SNMP/WinEventLog secrets. Every attempted port change is audited
/// (<see cref="AuditActions.ConfigChange"/>), the same as every other Settings write in this
/// product.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ListenerSettingsServiceTests
{
    private sealed class FixedUserAuthenticationStateProvider(string userName) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], "test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private static ListenerSettingsService BuildService(
        SqliteTestDatabase db, ListenerPortReloadService portReload, IngestionOptions ingestion) =>
        new(
            Options.Create(new TlsOptions()),
            Options.Create(new SnmpOptions()),
            Options.Create(new WinEventLogOptions()),
            Options.Create(ingestion),
            portReload,
            new SqliteSecretStore(db.Factory, new DpapiSecretProtector()),
            new SqliteAuditLog(db.Factory),
            new CurrentUserAccessor(new FixedUserAuthenticationStateProvider("admin@test")));

    [Fact]
    public async Task SetUdpTcpPortsAsync_ValidNewPort_RebindsLive_AndAudits()
    {
        int udpPort = LoopbackSyslog.FreeUdpPort();
        int tcpPort = LoopbackSyslog.FreeTcpPort();
        int newUdpPort = LoopbackSyslog.FreeUdpPort();
        string dataDir = Path.Combine(Path.GetTempPath(), "vsoftsol-listenersettings-" + Guid.NewGuid().ToString("N"));

        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = udpPort;
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = tcpPort;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        TcpSyslogListener tcp = h.AddTcpListener();
        await udp.StartAsync(default);
        await tcp.StartAsync(default);
        h.StartPipeline();

        try
        {
            var portReload = new ListenerPortReloadService(
                [udp, tcp],
                Options.Create(h.Options),
                Options.Create(new ActionExecutorOptions()),
                Options.Create(new CollectorOptions { DataDirectory = dataDir }),
                NullLogger<ListenerPortReloadService>.Instance);
            ListenerSettingsService service = BuildService(db, portReload, h.Options);

            (bool ok, string message) = await service.SetUdpTcpPortsAsync(newUdpPort, null, CancellationToken.None);

            ok.Should().BeTrue();
            message.Should().ContainEquivalentOf("no restart", "an applied live change should say so, not imply a restart is still needed");
            udp.BoundPort.Should().Be(newUdpPort);

            IReadOnlyList<AuditRecord> audited = await new SqliteAuditLog(db.Factory).QueryAsync(new AuditQuery(), CancellationToken.None);
            audited.Should().ContainSingle(a => a.Action == AuditActions.ConfigChange && a.Actor == "admin@test");
        }
        finally
        {
            await udp.StopAsync(default);
            await tcp.StopAsync(default);
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task SetUdpTcpPortsAsync_PortOutOfRange_RejectedBeforeTouchingAnything(int badPort)
    {
        int udpPort = LoopbackSyslog.FreeUdpPort();
        int tcpPort = LoopbackSyslog.FreeTcpPort();

        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using IngestionHarness h = await IngestionHarness.CreateAsync(o =>
        {
            o.UdpBindAddress = "127.0.0.1";
            o.UdpPort = udpPort;
            o.TcpBindAddress = "127.0.0.1";
            o.TcpPort = tcpPort;
        });
        UdpSyslogListener udp = h.AddUdpListener();
        TcpSyslogListener tcp = h.AddTcpListener();
        await udp.StartAsync(default);
        await tcp.StartAsync(default);
        h.StartPipeline();

        try
        {
            var portReload = new ListenerPortReloadService(
                [udp, tcp],
                Options.Create(h.Options),
                Options.Create(new ActionExecutorOptions()),
                Options.Create(new CollectorOptions { DataDirectory = Path.GetTempPath() }),
                NullLogger<ListenerPortReloadService>.Instance);
            ListenerSettingsService service = BuildService(db, portReload, h.Options);

            (bool ok, string _) = await service.SetUdpTcpPortsAsync(badPort, null, CancellationToken.None);

            ok.Should().BeFalse();
            udp.BoundPort.Should().Be(udpPort, "an invalid request must never reach the listener");

            long audited = await new SqliteAuditLog(db.Factory).CountAsync(CancellationToken.None);
            audited.Should().Be(0, "a rejected, no-op request is not a config change worth auditing");
        }
        finally
        {
            await udp.StopAsync(default);
            await tcp.StopAsync(default);
        }
    }
}
