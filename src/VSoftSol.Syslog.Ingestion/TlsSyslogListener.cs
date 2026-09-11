using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// TLS syslog listener (PHASE_11 item 1, RFC 5425). Structurally a
/// <see cref="TcpSyslogListener"/> whose per-connection stream is an authenticated
/// <see cref="SslStream"/> instead of the raw socket — the accept/connection-cap/idle-
/// timeout/framing logic is intentionally the same shape as the Phase 2 TCP listener
/// (boring code, not shared by inheritance, so the plain-TCP path is never at risk from a
/// TLS-only change).
/// </summary>
public sealed class TlsSyslogListener : ISyslogListener
{
    private readonly FrameIntake _intake;
    private readonly IngestionStatistics _stats;
    private readonly TlsCertificateProvider _certificateProvider;
    private readonly TlsOptions _tlsOptions;
    private readonly IngestionOptions _ingestionOptions;
    private readonly ILogger<TlsSyslogListener> _logger;

    private Socket? _listenSocket;
    private SemaphoreSlim? _connectionSlots;
    private CancellationTokenSource? _cts;
    private Task _acceptLoop = Task.CompletedTask;
    private readonly List<Task> _connections = [];
    private readonly object _connectionsLock = new();

    public TlsSyslogListener(
        FrameIntake intake,
        IngestionStatistics stats,
        TlsCertificateProvider certificateProvider,
        IOptions<TlsOptions> tlsOptions,
        IOptions<IngestionOptions> ingestionOptions,
        ILogger<TlsSyslogListener> logger)
    {
        _intake = intake;
        _stats = stats;
        _certificateProvider = certificateProvider;
        _tlsOptions = tlsOptions.Value;
        _ingestionOptions = ingestionOptions.Value;
        _logger = logger;
    }

    public string Name { get; private set; } = "tls";

    public Protocol Protocol => Protocol.Tls;

    public int BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(_tlsOptions.BindAddress);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(address, _tlsOptions.Port));
            socket.Listen(backlog: 512);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _listenSocket = socket;
        BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Name = $"tls:{_tlsOptions.BindAddress}:{BoundPort}";
        _connectionSlots = new SemaphoreSlim(_tlsOptions.MaxConnections, _tlsOptions.MaxConnections);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(socket, _cts.Token), CancellationToken.None);
        _logger.LogInformation("TLS syslog listener bound to {Endpoint}.", socket.LocalEndPoint);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "TLS accept error {Code}; continuing.", ex.SocketErrorCode);
                continue;
            }

            if (!_connectionSlots!.Wait(0, CancellationToken.None))
            {
                _logger.LogWarning("TLS connection cap ({Cap}) reached; rejecting {Remote}.", _tlsOptions.MaxConnections, SafeRemote(connection));
                connection.Dispose();
                continue;
            }

            Task handler = Task.Run(() => HandleConnectionAsync(connection, cancellationToken), CancellationToken.None);
            TrackConnection(handler);
        }
    }

    private async Task HandleConnectionAsync(Socket connection, CancellationToken cancellationToken)
    {
        string sourceIp = SafeRemote(connection);
        _stats.TcpConnectionOpened();
        SslStream? tls = null;

        try
        {
            connection.NoDelay = true;
            var network = new NetworkStream(connection, ownsSocket: false);
            tls = new SslStream(network, leaveInnerStreamOpen: false, ValidateClientCertificate);

            X509Certificate2? serverCertificate = await _certificateProvider(cancellationToken).ConfigureAwait(false);
            if (serverCertificate is null)
            {
                _logger.LogError("TLS listener has no server certificate configured; refusing connection from {Remote}.", sourceIp);
                return;
            }

            using var handshakeTimeout = new CancellationTokenSource(_tlsOptions.HandshakeTimeout);
            using var handshakeLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handshakeTimeout.Token);
            try
            {
                var authOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = _tlsOptions.RequireClientCertificate,
                    EnabledSslProtocols = AllowedProtocolsFrom(_tlsOptions.MinimumProtocol),
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                };
                await tls.AuthenticateAsServerAsync(authOptions, handshakeLinked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshakeTimeout.IsCancellationRequested)
            {
                _logger.LogWarning("TLS handshake with {Remote} timed out.", sourceIp);
                return;
            }
            catch (AuthenticationException ex)
            {
                // Includes the case a client offers only a protocol/cipher weaker than
                // EnabledSslProtocols — the handshake itself refuses it, so there is no
                // separate post-handshake "is it weak" check to get wrong.
                _logger.LogWarning(ex, "TLS handshake with {Remote} failed.", sourceIp);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "TLS handshake with {Remote} ended.", sourceIp);
                return;
            }

            await ReadFramedLoopAsync(tls, sourceIp, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "TLS connection {Remote} ended: {Code}.", sourceIp, ex.SocketErrorCode);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "TLS connection {Remote} I/O ended.", sourceIp);
        }
        finally
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                connection.Dispose();
            }

            _connectionSlots!.Release();
            _stats.TcpConnectionClosed();
        }
    }

    private async Task ReadFramedLoopAsync(SslStream tls, string sourceIp, CancellationToken cancellationToken)
    {
        var framer = new SyslogStreamFramer(_ingestionOptions.MaxMessageBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var pending = new List<(byte[] Payload, bool Truncated)>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(_tlsOptions.IdleTimeout);

                int read;
                try
                {
                    read = await tls.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("TLS connection {Remote} idle-timed-out.", sourceIp);
                    break;
                }

                if (read == 0)
                {
                    break; // peer closed
                }

                pending.Clear();
                try
                {
                    framer.Append(buffer.AsSpan(0, read), (payload, truncated) => pending.Add((payload, truncated)));
                }
                catch (FramingException ex)
                {
                    _logger.LogWarning("Dropping TLS connection {Remote}: {Reason}", sourceIp, ex.Message);
                    break;
                }

                foreach ((byte[] payload, bool truncated) in pending)
                {
                    var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.Tls, payload, truncated);
                    await _intake.AcceptAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }

            pending.Clear();
            framer.Flush((payload, truncated) => pending.Add((payload, truncated)));
            foreach ((byte[] payload, bool truncated) in pending)
            {
                var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.Tls, payload, truncated);
                await _intake.AcceptAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Mutual-TLS validation. When <see cref="TlsOptions.RequireClientCertificate"/>
    /// is off, any (or no) client certificate is accepted — the listener is server-auth-only.
    /// When on, a missing certificate or one whose thumbprint is not in the trusted allow-
    /// list is rejected outright (fail closed).</summary>
    private bool ValidateClientCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, System.Net.Security.SslPolicyErrors sslPolicyErrors)
    {
        if (!_tlsOptions.RequireClientCertificate)
        {
            return true;
        }

        if (certificate is null)
        {
            return false;
        }

        var cert2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        return _tlsOptions.TrustedClientCertificateThumbprints.Contains(cert2.Thumbprint, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>TLS 1.2 and 1.3 are always allowed once the minimum is at least TLS 1.2
    /// (there is no "1.2 only, not 1.3" use case); a minimum of TLS 1.3 excludes 1.2.
    /// TLS 1.0/1.1 and SSL are never offered, regardless of configuration.</summary>
    private static SslProtocols AllowedProtocolsFrom(SslProtocols minimum) =>
        minimum >= SslProtocols.Tls13 ? SslProtocols.Tls13 : SslProtocols.Tls12 | SslProtocols.Tls13;

    private void TrackConnection(Task handler)
    {
        lock (_connectionsLock)
        {
            _connections.RemoveAll(t => t.IsCompleted);
            _connections.Add(handler);
        }
    }

    private static string SafeRemote(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "unknown";
        }
        catch (SocketException)
        {
            return "unknown";
        }
        catch (ObjectDisposedException)
        {
            return "unknown";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listenSocket?.Close();

        Task[] outstanding;
        lock (_connectionsLock)
        {
            outstanding = [.. _connections];
        }

        try
        {
            await Task.WhenAll([_acceptLoop, .. outstanding]).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while stopping TLS listener {Name}.", Name);
        }

        _logger.LogInformation("TLS syslog listener {Name} stopped.", Name);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing TLS listener.");
        }

        _cts?.Dispose();
        _connectionSlots?.Dispose();
        _listenSocket?.Dispose();
    }
}
