using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Snmp;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// SNMP trap receiver (PHASE_11 item 2, RFC 1157 / RFC 3416). Structurally an
/// <see cref="UdpSyslogListener"/> that decodes each datagram far enough to check its
/// community string before accepting it — a wrong community is dropped exactly like an
/// SNMP agent drops it, silently but counted, never propagated as an error. The socket
/// always binds (so the reserved port is visible even when misconfigured); a missing or
/// still-default (<c>public</c>) community makes every datagram refused rather than
/// preventing the collector host from starting.
/// </summary>
public sealed class SnmpTrapListener : ISyslogListener
{
    private const int SioUdpConnReset = -1744830452; // 0x9800000C

    private readonly FrameIntake _intake;
    private readonly IngestionStatistics _stats;
    private readonly SnmpCommunityProvider _communities;
    private readonly SnmpOptions _options;
    private readonly ILogger<SnmpTrapListener> _logger;

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task _receiveLoop = Task.CompletedTask;
    private bool _warnedNoCommunity;

    public SnmpTrapListener(
        FrameIntake intake,
        IngestionStatistics stats,
        SnmpCommunityProvider communities,
        IOptions<SnmpOptions> options,
        ILogger<SnmpTrapListener> logger)
    {
        _intake = intake;
        _stats = stats;
        _communities = communities;
        _options = options.Value;
        _logger = logger;
    }

    public string Name { get; private set; } = "snmp";

    public Protocol Protocol => Protocol.Snmp;

    public int BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(_options.BindAddress);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            socket.Bind(new IPEndPoint(address, _options.Port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Name = $"snmp:{_options.BindAddress}:{BoundPort}";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token), CancellationToken.None);
        _logger.LogInformation("SNMP trap listener bound to {Endpoint}.", socket.LocalEndPoint);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        const int rentSize = 65_536; // SNMP traps are small; a UDP datagram is at most 65507 bytes anyway
        EndPoint remote = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(rentSize);
            try
            {
                SocketReceiveFromResult result =
                    await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);

                string sourceIp = ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
                await HandleDatagramAsync(buffer.AsMemory(0, result.ReceivedBytes), sourceIp, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "SNMP receive error {Code}; continuing.", ex.SocketErrorCode);
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

    private async Task HandleDatagramAsync(ReadOnlyMemory<byte> datagram, string sourceIp, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> allowed = await _communities(cancellationToken).ConfigureAwait(false);
        if (allowed.Count == 0 || allowed.Contains("public", StringComparer.Ordinal))
        {
            if (!_warnedNoCommunity)
            {
                _logger.LogWarning(
                    "SNMP listener has no non-default community configured; refusing all traps until one is set in Settings.");
                _warnedNoCommunity = true;
            }

            _stats.Listener(Name).AddFailed(1);
            return;
        }

        if (!SnmpBerReader.TryParse(datagram.Span, out SnmpTrapMessage? message, out string? error,
                _options.MaxVarbindsPerTrap, _options.MaxOidArcs))
        {
            _logger.LogDebug("Dropping malformed SNMP datagram from {Remote}: {Reason}", sourceIp, error);
            _stats.Listener(Name).AddFailed(1);
            return;
        }

        if (!allowed.Contains(message.Community, StringComparer.Ordinal))
        {
            _logger.LogDebug("Dropping SNMP trap from {Remote}: community string did not match.", sourceIp);
            _stats.Listener(Name).AddFailed(1);
            return;
        }

        // Community validated — hand the ORIGINAL bytes to the pipeline (Constraint 4);
        // the normalizer re-decodes them downstream, its own MessageParser.Parse step.
        var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.Snmp, datagram.ToArray(), truncated: false);
        await _intake.AcceptAsync(frame, cancellationToken).ConfigureAwait(false);
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

        _logger.LogInformation("SNMP trap listener {Name} stopped.", Name);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing SNMP listener.");
        }

        _cts?.Dispose();
        _socket?.Dispose();
    }
}
