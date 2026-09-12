using FluentAssertions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Listeners;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hosting;

/// <summary>
/// v1.1 — P2-1: on collector startup, upserts one `listeners` row per protocol (from
/// configuration, not the bound socket — real installs never use an ephemeral port 0, and
/// this way registration does not need to wait for <c>IngestionHostedService</c> to finish
/// starting listeners) and populates <see cref="ListenerIdRegistry"/> so
/// <see cref="EventEnricher"/> can resolve `SyslogEvent.ListenerId`.
/// </summary>
[Trait("Category", "Hosting")]
public sealed class ListenerRegistrationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_RegistersAllFiveProtocols_AndPopulatesTheRegistry()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteListenerStore(db.Factory);
        var registry = new ListenerIdRegistry();

        var service = new ListenerRegistrationHostedService(
            store,
            registry,
            Options.Create(new IngestionOptions { UdpBindAddress = "0.0.0.0", UdpPort = 514, UdpEnabled = true, TcpBindAddress = "0.0.0.0", TcpPort = 514, TcpEnabled = true }),
            Options.Create(new TlsOptions { BindAddress = "0.0.0.0", Port = 6514, Enabled = false }),
            Options.Create(new SnmpOptions { BindAddress = "0.0.0.0", Port = 162, Enabled = false }),
            Options.Create(new WinEventLogOptions { BindAddress = "0.0.0.0", Port = 8443, Enabled = false }));

        await service.StartAsync(CancellationToken.None);

        IReadOnlyList<ListenerRecord> rows = await store.ListAsync(CancellationToken.None);
        rows.Should().HaveCount(5);
        rows.Select(r => r.Protocol).Should().BeEquivalentTo(
            [Protocol.Udp, Protocol.Tcp, Protocol.Tls, Protocol.Snmp, Protocol.WinEventLog]);
        rows.Should().ContainSingle(r => r.Protocol == Protocol.Udp && r.Enabled);
        rows.Should().ContainSingle(r => r.Protocol == Protocol.Tls && !r.Enabled);

        foreach (Protocol protocol in new[] { Protocol.Udp, Protocol.Tcp, Protocol.Tls, Protocol.Snmp, Protocol.WinEventLog })
        {
            registry.TryGetId(protocol, out long id).Should().BeTrue();
            id.Should().Be(rows.Single(r => r.Protocol == protocol).ListenerId);
        }
    }

    [Fact]
    public async Task StartAsync_CalledTwiceWithUnchangedConfig_ReusesTheSameIds()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteListenerStore(db.Factory);
        var registry = new ListenerIdRegistry();
        IOptions<IngestionOptions> ingestion = Options.Create(new IngestionOptions
        {
            UdpBindAddress = "0.0.0.0",
            UdpPort = 514,
            UdpEnabled = true,
            TcpBindAddress = "0.0.0.0",
            TcpPort = 514,
            TcpEnabled = true,
        });
        IOptions<TlsOptions> tls = Options.Create(new TlsOptions());
        IOptions<SnmpOptions> snmp = Options.Create(new SnmpOptions());
        IOptions<WinEventLogOptions> winEventLog = Options.Create(new WinEventLogOptions());

        var first = new ListenerRegistrationHostedService(store, registry, ingestion, tls, snmp, winEventLog);
        await first.StartAsync(CancellationToken.None);
        registry.TryGetId(Protocol.Udp, out long idBefore);

        var second = new ListenerRegistrationHostedService(store, registry, ingestion, tls, snmp, winEventLog);
        await second.StartAsync(CancellationToken.None);
        registry.TryGetId(Protocol.Udp, out long idAfter);

        idAfter.Should().Be(idBefore, "a restart with unchanged config is the same listener identity");
        (await store.ListAsync(CancellationToken.None)).Should().HaveCount(5, "no duplicate rows across the two startups");
    }
}
