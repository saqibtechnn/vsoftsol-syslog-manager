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
    }
}
