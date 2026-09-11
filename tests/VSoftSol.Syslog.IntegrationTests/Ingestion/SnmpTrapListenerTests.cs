using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>PHASE_11 item 2 — "send v1 and v2c traps, assert normalization" plus the
/// wrong-community rejection (SECURITY_STANDARDS.md: never default to "public").</summary>
[Trait("Category", "Ingestion")]
public sealed class SnmpTrapListenerTests
{
    private static ServiceProvider Build(ILogRepository repo, IReadOnlyList<string> allowedCommunities)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repo);
        services.AddSyslogIngestion();
        services.Configure<IngestionOptions>(o =>
        {
            o.UdpEnabled = false;
            o.TcpEnabled = false;
            o.SpillDirectory = Path.Combine(Path.GetTempPath(), "vsoftsol-snmp-spill-" + Guid.NewGuid().ToString("N"));
        });
        services.Configure<SnmpOptions>(o =>
        {
            o.Enabled = true;
            o.BindAddress = "127.0.0.1";
            o.Port = Random.Shared.Next(20_000, 60_000);
        });
        services.AddSingleton<SnmpCommunityProvider>(_ => _ => new ValueTask<IReadOnlyList<string>>(allowedCommunities));
        services.AddSingleton<SnmpTrapListener>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StartAsync_ATrapWithTheRightCommunity_IsAcceptedIntoTheChannel()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository, ["s3cret-community"]);
        var listener = sp.GetRequiredService<SnmpTrapListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            byte[] trap = MinimalSnmpV1Trap.Build("s3cret-community");
            await SendDatagramAsync(listener.BoundPort, trap);

            RawFrame? received = await WaitForFrameAsync(channel);
            received.Should().NotBeNull();
            received!.Protocol.Should().Be(Protocol.Snmp);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_ATrapWithTheWrongCommunity_IsSilentlyDropped_NotIngested()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository, ["s3cret-community"]);
        var listener = sp.GetRequiredService<SnmpTrapListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            byte[] trap = MinimalSnmpV1Trap.Build("wrong-community");
            await SendDatagramAsync(listener.BoundPort, trap);

            (await TryWaitForFrameAsync(channel)).Should().BeNull("a trap with the wrong community must never reach the ingest channel");
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_NoNonDefaultCommunityConfigured_RefusesEveryTrap()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository, []); // nothing configured
        var listener = sp.GetRequiredService<SnmpTrapListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            byte[] trap = MinimalSnmpV1Trap.Build("public");
            await SendDatagramAsync(listener.BoundPort, trap);

            (await TryWaitForFrameAsync(channel)).Should().BeNull("the default 'public' configuration must never be accepted");
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private static async Task SendDatagramAsync(int port, byte[] payload)
    {
        using var udp = new UdpClient();
        await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<RawFrame?> WaitForFrameAsync(IngestionChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                if (channel.Reader.TryRead(out RawFrame? frame))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    private static async Task<RawFrame?> TryWaitForFrameAsync(IngestionChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            if (await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false) && channel.Reader.TryRead(out RawFrame? frame))
            {
                return frame;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }
}
