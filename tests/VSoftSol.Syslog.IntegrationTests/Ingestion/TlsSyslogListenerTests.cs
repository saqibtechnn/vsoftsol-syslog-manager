using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>PHASE_11 item 1 / "TLS handshake test including a rejected client under mutual TLS."</summary>
[Trait("Category", "Ingestion")]
public sealed class TlsSyslogListenerTests
{
    private static ServiceProvider Build(ILogRepository repo, X509Certificate2 serverCert, Action<TlsOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repo);
        services.AddSyslogIngestion();
        services.Configure<IngestionOptions>(o =>
        {
            o.UdpEnabled = false;
            o.TcpEnabled = false;
            o.SpillDirectory = Path.Combine(Path.GetTempPath(), "vsoftsol-tls-spill-" + Guid.NewGuid().ToString("N"));
        });
        services.Configure<TlsOptions>(o =>
        {
            o.Enabled = true;
            o.BindAddress = "127.0.0.1";
            o.Port = Random.Shared.Next(20_000, 60_000); // TlsOptions.Port requires 1-65535, so unlike Udp/Tcp's own ephemeral-port tests this cannot be 0
            configure?.Invoke(o);
        });
        services.AddSingleton<TlsCertificateProvider>(_ => _ => new ValueTask<X509Certificate2?>(serverCert));
        services.AddSingleton<TlsSyslogListener>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StartAsync_AFramedMessageOverTls_IsAcceptedIntoTheChannel()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        using X509Certificate2 serverCert = SelfSignedCertificate.Create();
        await using ServiceProvider sp = Build(db.Repository, serverCert);
        var listener = sp.GetRequiredService<TlsSyslogListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            await SendOverTlsAsync(listener.BoundPort, "<134>Mar  1 10:00:00 host tag: tls test message\n", requireClientCert: false);

            RawFrame? received = await WaitForFrameAsync(channel);
            received.Should().NotBeNull();
            received!.Protocol.Should().Be(Protocol.Tls);
            Encoding.UTF8.GetString(received.Payload.Span).Should().Contain("tls test message");
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_MutualTls_AnUntrustedClientCertificate_IsRejected()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        using X509Certificate2 serverCert = SelfSignedCertificate.Create();
        using X509Certificate2 trustedClientCert = SelfSignedCertificate.Create("trusted-client");
        using X509Certificate2 untrustedClientCert = SelfSignedCertificate.Create("untrusted-client");

        await using ServiceProvider sp = Build(db.Repository, serverCert, o =>
        {
            o.RequireClientCertificate = true;
            o.TrustedClientCertificateThumbprints = [trustedClientCert.Thumbprint];
        });
        var listener = sp.GetRequiredService<TlsSyslogListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            // The client may see the rejection as a handshake exception, or (loopback
            // timing-dependent) as an apparently-successful handshake whose subsequent
            // write lands on a connection the server has already torn down — either way is
            // an acceptable client-side symptom. The one property that actually matters is
            // asserted unconditionally below: the message must never reach the ingest channel.
            try
            {
                await SendOverTlsAsync(listener.BoundPort, "<134>should never arrive\n", requireClientCert: true, untrustedClientCert);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
            {
            }

            (await TryWaitForFrameAsync(channel)).Should().BeNull("an untrusted client certificate under mutual TLS must never reach the ingest channel");
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_MutualTls_ATrustedClientCertificate_Succeeds()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        using X509Certificate2 serverCert = SelfSignedCertificate.Create();
        using X509Certificate2 trustedClientCert = SelfSignedCertificate.Create("trusted-client");

        await using ServiceProvider sp = Build(db.Repository, serverCert, o =>
        {
            o.RequireClientCertificate = true;
            o.TrustedClientCertificateThumbprints = [trustedClientCert.Thumbprint];
        });
        var listener = sp.GetRequiredService<TlsSyslogListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            await SendOverTlsAsync(listener.BoundPort, "<134>trusted client message\n", requireClientCert: true, trustedClientCert);

            RawFrame? received = await WaitForFrameAsync(channel);
            received.Should().NotBeNull();
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private static async Task SendOverTlsAsync(int port, string message, bool requireClientCert, X509Certificate2? clientCert = null)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
#pragma warning disable CA5359 // test client only — the server presents a throwaway self-signed cert with no real CA to validate against.
        using var ssl = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
#pragma warning restore CA5359
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "test-collector",
            EnabledSslProtocols = SslProtocols.Tls12,
        };
        if (requireClientCert && clientCert is not null)
        {
            options.ClientCertificates = [clientCert];
        }

        await ssl.AuthenticateAsClientAsync(options).ConfigureAwait(false);
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await ssl.WriteAsync(bytes).ConfigureAwait(false);
        await ssl.FlushAsync().ConfigureAwait(false);
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
