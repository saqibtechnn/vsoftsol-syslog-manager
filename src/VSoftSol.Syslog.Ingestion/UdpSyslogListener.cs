using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// UDP syslog listener (PHASE_02 item 1). One datagram = one <see cref="RawFrame"/>. The
/// receive loop rents its buffer from <see cref="ArrayPool{T}"/> and copies only the exact
/// bytes received, so a steady stream allocates nothing but the owned payload arrays.
/// </summary>
public sealed class UdpSyslogListener : ISyslogListener
{
    private const int SioUdpConnReset = -1744830452; // 0x9800000C

    private readonly FrameIntake _intake;
    private readonly IngestionOptions _options;
    private readonly ILogger<UdpSyslogListener> _logger;

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task _receiveLoop = Task.CompletedTask;
    private readonly SemaphoreSlim _rebindLock = new(1, 1);

    public UdpSyslogListener(FrameIntake intake, IOptions<IngestionOptions> options, ILogger<UdpSyslogListener> logger)
    {
        _intake = intake;
        _options = options.Value;
        _logger = logger;
    }

    public string Name { get; private set; } = "udp";

    public Protocol Protocol => Protocol.Udp;

    public int BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(_options.UdpBindAddress);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ReceiveBufferSize = _options.UdpReceiveBufferBytes;

            // Windows: a prior send to an unreachable port otherwise makes the *next*
            // ReceiveFrom throw ConnectionReset. Suppress it (RFC-irrelevant for a server).
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            socket.Bind(new IPEndPoint(address, _options.UdpPort));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Name = $"udp:{_options.UdpBindAddress}:{BoundPort}";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token), CancellationToken.None);
        _logger.LogInformation("UDP syslog listener bound to {Endpoint}.", socket.LocalEndPoint);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        int cap = _options.MaxMessageBytes;

        // A UDP payload can be up to 65507 bytes. Always receive the whole datagram (so it
        // is never truncated by the socket layer, which would raise SocketError.MessageSize
        // and lose it), then trim to MaxMessageBytes when building the frame.
        int rentSize = Math.Max(cap + 1, 65_536);
        EndPoint remote = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(rentSize);
            try
            {
                SocketReceiveFromResult result =
                    await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);

                int n = result.ReceivedBytes;
                bool truncated = n > cap;
                int length = truncated ? cap : n;

                // A zero-length datagram is legal on the wire and carries no message.
                var payload = new byte[length];
                Buffer.BlockCopy(buffer, 0, payload, 0, length);

                string sourceIp = ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
                var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.Udp, payload, truncated);
                await _intake.AcceptAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows ICMP-port-unreachable artefact; ignore and keep receiving.
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "UDP receive error {Code}; continuing.", ex.SocketErrorCode);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _socket?.Close();

        try
        {
            await _receiveLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogInformation("UDP syslog listener {Name} stopped.", Name);
    }

    /// <summary>
    /// Moves this listener to <paramref name="newPort"/> without a process restart (v1.1
    /// live listener ports). Binds the new socket <em>before</em> touching the old one, so a
    /// bind failure (port in use, no permission) throws with the original listener still
    /// fully running — this protocol is never left with zero listeners
    /// (CLAUDE.md Constraint 3). <paramref name="cancellationToken"/> only governs waiting
    /// for the old receive loop to retire; it must not be a request-scoped token whose
    /// cancellation could reach the new, otherwise-unrelated receive loop.
    /// </summary>
    public async Task RebindAsync(int newPort, CancellationToken cancellationToken)
    {
        await _rebindLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IPAddress address = IPAddress.Parse(_options.UdpBindAddress);
            var newSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                newSocket.ReceiveBufferSize = _options.UdpReceiveBufferBytes;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    newSocket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                }

                newSocket.Bind(new IPEndPoint(address, newPort));
            }
            catch
            {
                newSocket.Dispose();
                throw;
            }

            // The new socket is bound and about to receive — only now is it safe to retire
            // the old one, so there is never a gap with nothing listening on this protocol.
            Socket? oldSocket = _socket;
            CancellationTokenSource? oldCts = _cts;
            Task oldLoop = _receiveLoop;

            _socket = newSocket;
            BoundPort = ((IPEndPoint)newSocket.LocalEndPoint!).Port;
            Name = $"udp:{_options.UdpBindAddress}:{BoundPort}";
            var newCts = new CancellationTokenSource();
            _cts = newCts;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(newSocket, newCts.Token), CancellationToken.None);
            _logger.LogInformation("UDP syslog listener rebound to {Endpoint}.", newSocket.LocalEndPoint);

            if (oldCts is not null)
            {
                await oldCts.CancelAsync().ConfigureAwait(false);
            }

            oldSocket?.Close();

            try
            {
                await oldLoop.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            oldCts?.Dispose();
            oldSocket?.Dispose();
        }
        finally
        {
            _rebindLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing UDP listener.");
        }

        _cts?.Dispose();
        _socket?.Dispose();
        _rebindLock.Dispose();
    }
}
