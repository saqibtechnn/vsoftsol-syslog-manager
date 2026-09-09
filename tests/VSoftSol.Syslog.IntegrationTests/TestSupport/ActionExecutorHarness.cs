using System.Net;
using System.Net.Sockets;
using System.Text;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Actions;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>In-process stubs and helpers for the Phase 7 action fault-injection tests.</summary>
internal static class ActionTestSupport
{
    public static SyslogEvent Event(string message = "disk full on /var", string? hostname = "core-sw-1") => new()
    {
        ReceivedUtc = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero),
        SourceIp = "203.0.113.5",
        Hostname = hostname,
        AppName = "kernel",
        Severity = Severity.Critical,
        Facility = Facility.Local0,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc3164,
    };

    public static ActionContext Context(
        RuleAction action,
        SyslogEvent? evt = null,
        ActionExecutorOptions? options = null,
        SecretResolver? secrets = null,
        NotificationSink? notifications = null) => new(
            RuleId: 1,
            RuleName: "test rule",
            Action: action,
            Event: evt ?? Event(),
            Secrets: secrets ?? ((_, _) => ValueTask.FromResult<string?>(null)),
            Notifications: notifications ?? ((_, _, _, _, _) => ValueTask.CompletedTask),
            Options: options ?? new ActionExecutorOptions());

    /// <summary>
    /// A webhook action + options wired to reach a loopback test sink — the SSRF guard
    /// would otherwise (correctly) refuse <c>127.0.0.1</c>. Used only by the tests that
    /// exercise webhook <em>behaviour</em>, never the refusal tests.
    /// </summary>
    public static (HttpWebhookAction Action, ActionExecutorOptions Options) LoopbackWebhook(
        string url, string bodyTemplate = "{{}}", int timeoutSeconds = 10)
    {
        var action = new HttpWebhookAction
        {
            Url = url,
            BodyTemplate = bodyTemplate,
            TimeoutSeconds = timeoutSeconds,
            AllowPrivateNetwork = true,
        };
        var options = new ActionExecutorOptions { WebhookPrivateNetworkAllowList = ["127.0.0.0/8", "::1/128"] };
        return (action, options);
    }

    /// <summary>Locates the built action-probe fixture executable.</summary>
    public static string ProbeExecutable()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = testBin.Parent!.Name;
        string tfm = testBin.Name;
        DirectoryInfo testsRoot = testBin.Parent!.Parent!.Parent!.Parent!;
        string exeName = OperatingSystem.IsWindows() ? "action-probe.exe" : "action-probe";
        string candidate = Path.Combine(testsRoot.FullName, "VSoftSol.Syslog.ActionProbe", "bin", configuration, tfm, exeName);
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"action-probe not built. Looked at: {candidate}");
        }

        return candidate;
    }
}

/// <summary>A minimal SMTP sink that accepts one message and records it, or refuses/hangs on demand.</summary>
internal sealed class SmtpSink : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public SmtpSink(SmtpBehaviour behaviour = SmtpBehaviour.Accept)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(behaviour, _cts.Token));
    }

    public int Port { get; }

    public List<string> ReceivedData { get; } = [];

    private async Task AcceptLoopAsync(SmtpBehaviour behaviour, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, behaviour, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, SmtpBehaviour behaviour, CancellationToken ct)
    {
        using (client)
        {
            if (behaviour == SmtpBehaviour.RefuseAfterConnect)
            {
                return; // drop
            }

            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            await writer.WriteLineAsync("220 test ESMTP");
            bool inData = false;
            var body = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        lock (ReceivedData)
                        {
                            ReceivedData.Add(body.ToString());
                        }

                        await writer.WriteLineAsync(behaviour == SmtpBehaviour.Reject5xx ? "550 rejected" : "250 OK");
                        continue;
                    }

                    body.AppendLine(line);
                    continue;
                }

                string verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "MAIL":
                    case "RCPT":
                        await writer.WriteLineAsync(behaviour == SmtpBehaviour.Reject4xx ? "451 try later" : "250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 send data");
                        inData = true;
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}

internal enum SmtpBehaviour
{
    Accept,
    RefuseAfterConnect,
    Reject4xx,
    Reject5xx,
}

/// <summary>A one-shot HTTP sink over <see cref="HttpListener"/> with configurable behaviour.</summary>
internal sealed class HttpSink : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public HttpSink(Func<HttpListenerContext, Task> handler)
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);
                try
                {
                    await handler(ctx).ConfigureAwait(false);
                }
                catch
                {
                    // ignore — the test asserts on the client side
                }
                finally
                {
                    try { ctx.Response.Close(); } catch { /* already closed */ }
                }
            }
        });
    }

    private int _requestCount;

    public int Port { get; }

    public int RequestCount => _requestCount;

    public string Url => $"http://127.0.0.1:{Port}/hook";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch
        {
        }

        _cts.Dispose();
    }
}

/// <summary>A UDP receiver for the forward-action test.</summary>
internal sealed class UdpSink : IDisposable
{
    private readonly UdpClient _udp;

    public UdpSink()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public async Task<byte[]> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        UdpReceiveResult result = await _udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
        return result.Buffer;
    }

    public void Dispose() => _udp.Dispose();
}
