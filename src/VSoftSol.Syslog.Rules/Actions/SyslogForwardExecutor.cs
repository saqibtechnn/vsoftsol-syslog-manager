using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Relays the event to another syslog collector over UDP / TCP / TLS (PHASE_07 item 3
/// "ForwardSyslog"). Loop-protected: a target that matches one of the collector's own
/// listener endpoints is refused (never amplified). With no template the raw bytes are
/// forwarded verbatim (Constraint 4); a template renders a replacement line.
/// </summary>
internal sealed class SyslogForwardExecutor : IActionExecutor
{
    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (ForwardSyslogAction)context.Action;

        if (string.IsNullOrWhiteSpace(action.Host) || action.Port is < 1 or > 65535)
        {
            return ActionResult.Permanent("the forward target is missing a host or port");
        }

        if (IsLoop(action, context.Options.LocalSyslogEndpoints))
        {
            return ActionResult.Permanent(
                $"{action.Host}:{action.Port} is one of this collector's own listeners — forwarding there would loop");
        }

        byte[] payload = string.IsNullOrEmpty(action.Template)
            ? context.Event.RawMessage.ToArray()
            : Encoding.UTF8.GetBytes(FieldTemplate.Render(action.Template, context.Event).ReplaceLineEndings(" "));

        try
        {
            switch (action.Transport)
            {
                case ForwardTransport.Udp:
                    await SendUdpAsync(action.Host, action.Port, payload, cancellationToken).ConfigureAwait(false);
                    break;
                case ForwardTransport.Tcp:
                    await SendStreamAsync(action.Host, action.Port, payload, tls: false, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await SendStreamAsync(action.Host, action.Port, payload, tls: true, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            return ActionResult.Transient($"forward to {action.Host}:{action.Port} failed: {ex.Message}");
        }

        return ActionResult.Success($"forwarded {payload.Length} bytes to {action.Host}:{action.Port} ({action.Transport})");
    }

    private static bool IsLoop(ForwardSyslogAction action, IReadOnlyList<string> localEndpoints)
    {
        string candidate = $"{action.Host.Trim()}:{action.Port}";
        if (localEndpoints.Any(e => string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // A literal loopback address on any local listener port is also a loop.
        if (IPAddress.TryParse(action.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip))
        {
            return localEndpoints.Any(e => e.EndsWith($":{action.Port}", StringComparison.Ordinal));
        }

        return false;
    }

    private static async Task SendUdpAsync(string host, int port, byte[] payload, CancellationToken ct)
    {
        using var udp = new UdpClient();
        await udp.SendAsync(payload, host, port, ct).ConfigureAwait(false);
    }

    private static async Task SendStreamAsync(string host, int port, byte[] payload, bool tls, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

        Stream stream = client.GetStream();
        if (tls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(host).ConfigureAwait(false);
            stream = ssl;
        }

        await using (stream.ConfigureAwait(false))
        {
            // Octet-counting frame (RFC 6587) so the receiver can delimit the message.
            byte[] framed = Encoding.ASCII.GetBytes($"{payload.Length} ");
            await stream.WriteAsync(framed, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
