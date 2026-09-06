using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// TCP syslog listener (PHASE_02 item 2). Each connection gets its own read loop and
/// <see cref="SyslogStreamFramer"/> (newline or RFC 6587 octet-counting, auto-detected).
/// A connection cap and an idle timeout bound the cost of connection floods and slowloris.
/// </summary>
public sealed class TcpSyslogListener : ISyslogListener
{
    private readonly FrameIntake _intake;
    private readonly IngestionStatistics _stats;
    private readonly IngestionOptions _options;
    private readonly ILogger<TcpSyslogListener> _logger;

    private Socket? _listenSocket;
    private SemaphoreSlim? _connectionSlots;
    private CancellationTokenSource? _cts;
    private Task _acceptLoop = Task.CompletedTask;
    private readonly List<Task> _connections = [];
    private readonly object _connectionsLock = new();

    public TcpSyslogListener(
        FrameIntake intake,
        IngestionStatistics stats,
        IOptions<IngestionOptions> options,
        ILogger<TcpSyslogListener> logger)
    {
        _intake = intake;
        _stats = stats;
        _options = options.Value;
        _logger = logger;
    }

    public string Name { get; private set; } = "tcp";

    public Protocol Protocol => Protocol.Tcp;

    public int BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(_options.TcpBindAddress);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(address, _options.TcpPort));
            socket.Listen(backlog: 512);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _listenSocket = socket;
        BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Name = $"tcp:{_options.TcpBindAddress}:{BoundPort}";
        _connectionSlots = new SemaphoreSlim(_options.TcpMaxConnections, _options.TcpMaxConnections);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(socket, _cts.Token), CancellationToken.None);
        _logger.LogInformation("TCP syslog listener bound to {Endpoint}.", socket.LocalEndPoint);
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
                _logger.LogDebug(ex, "TCP accept error {Code}; continuing.", ex.SocketErrorCode);
                continue;
            }

            if (!_connectionSlots!.Wait(0, CancellationToken.None))
            {
                _logger.LogWarning("TCP connection cap ({Cap}) reached; rejecting {Remote}.",
                    _options.TcpMaxConnections, SafeRemote(connection));
                connection.Dispose();
                continue;
            }

            Task handler = Task.Run(() => HandleConnectionAsync(connection, cancellationToken), CancellationToken.None);
            TrackConnection(handler);
        }
    }

    private async Task HandleConnectionAsync(Socket connection, CancellationToken cancellationToken)
    {
        _stats.TcpConnectionOpened();
        string sourceIp = SafeRemote(connection);
        var framer = new SyslogStreamFramer(_options.MaxMessageBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var pending = new List<(byte[] Payload, bool Truncated)>();

        try
        {
            connection.NoDelay = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(_options.TcpIdleTimeout);

                int read;
                try
                {
                    read = await connection.ReceiveAsync(buffer, SocketFlags.None, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("TCP connection {Remote} idle-timed-out.", sourceIp);
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
                    _logger.LogWarning("Dropping TCP connection {Remote}: {Reason}", sourceIp, ex.Message);
                    break;
                }

                foreach ((byte[] payload, bool truncated) in pending)
                {
                    var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.Tcp, payload, truncated);
                    await _intake.AcceptAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }

            pending.Clear();
            framer.Flush((payload, truncated) => pending.Add((payload, truncated)));
            foreach ((byte[] payload, bool truncated) in pending)
            {
                var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.Tcp, payload, truncated);
                await _intake.AcceptAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "TCP connection {Remote} ended: {Code}.", sourceIp, ex.SocketErrorCode);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "TCP connection {Remote} I/O ended.", sourceIp);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            connection.Dispose();
            _connectionSlots!.Release();
            _stats.TcpConnectionClosed();
        }
    }

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
            _logger.LogDebug(ex, "Error while stopping TCP listener {Name}.", Name);
        }

        _logger.LogInformation("TCP syslog listener {Name} stopped.", Name);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing TCP listener.");
        }

        _cts?.Dispose();
        _connectionSlots?.Dispose();
        _listenSocket?.Dispose();
    }
}
