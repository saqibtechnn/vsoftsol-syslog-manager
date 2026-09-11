using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Windows Event Log intake endpoint (PHASE_11 item 3). Built on the in-box
/// <see cref="HttpListener"/> — no ASP.NET dependency for one small authenticated POST
/// endpoint — so it fits <see cref="ISyslogListener"/> exactly like every other listener:
/// started and stopped by <see cref="IngestionHostedService"/>, feeding the same
/// <see cref="FrameIntake"/> and therefore the same durability guarantees (spill queue,
/// batching, dedup) as UDP/TCP/TLS.
/// </summary>
public sealed class WinEventLogListener : ISyslogListener
{
    private const string RequestPath = "/wineventlog";
    private const string ApiKeyHeader = "X-Api-Key";

    private readonly FrameIntake _intake;
    private readonly IngestionStatistics _stats;
    private readonly WinEventLogApiKeyValidator _apiKeys;
    private readonly WinEventLogOptions _options;
    private readonly ILogger<WinEventLogListener> _logger;
    private readonly ConcurrentDictionary<string, MinuteBucket> _rateBuckets = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task _acceptLoop = Task.CompletedTask;

    public WinEventLogListener(
        FrameIntake intake,
        IngestionStatistics stats,
        WinEventLogApiKeyValidator apiKeys,
        IOptions<WinEventLogOptions> options,
        ILogger<WinEventLogListener> logger)
    {
        _intake = intake;
        _stats = stats;
        _apiKeys = apiKeys;
        _options = options.Value;
        _logger = logger;
    }

    public string Name { get; private set; } = "wineventlog";

    public Protocol Protocol => Protocol.WinEventLog;

    public int BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        string prefix = $"http://{_options.BindAddress}:{_options.Port}/";
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Close();
            throw;
        }

        _listener = listener;
        BoundPort = _options.Port;
        Name = $"wineventlog:{_options.BindAddress}:{BoundPort}";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
        _logger.LogInformation("Windows Event Log intake listener bound to {Prefix}.", prefix);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        string sourceIp = context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        try
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != RequestPath)
            {
                Respond(context, HttpStatusCode.NotFound);
                return;
            }

            string? apiKey = context.Request.Headers[ApiKeyHeader];
            if (string.IsNullOrEmpty(apiKey) || !await _apiKeys(apiKey, sourceIp, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Windows Event Log intake: rejected an unauthorized request from {Remote}.", sourceIp);
                _stats.Listener(Name).AddFailed(1);
                Respond(context, HttpStatusCode.Unauthorized);
                return;
            }

            if (!AllowByRate(sourceIp))
            {
                _stats.Listener(Name).AddFailed(1);
                Respond(context, HttpStatusCode.TooManyRequests);
                return;
            }

            if (context.Request.ContentLength64 <= 0 || context.Request.ContentLength64 > _options.MaxBodyBytes)
            {
                Respond(context, HttpStatusCode.RequestEntityTooLarge);
                return;
            }

            byte[] body;
            using (var buffer = new MemoryStream())
            {
                await context.Request.InputStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                body = buffer.ToArray();
            }

            var frame = new RawFrame(DateTimeOffset.UtcNow, sourceIp, Name, Protocol.WinEventLog, body, truncated: false);
            await _intake.AcceptAsync(frame, cancellationToken).ConfigureAwait(false);
            Respond(context, HttpStatusCode.Accepted);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling a Windows Event Log request from {Remote}.", sourceIp);
            try
            {
                Respond(context, HttpStatusCode.InternalServerError);
            }
            catch (Exception closeEx)
            {
                _logger.LogDebug(closeEx, "Error closing a Windows Event Log response to {Remote}.", sourceIp);
            }
        }
    }

    private static void Respond(HttpListenerContext context, HttpStatusCode status)
    {
        context.Response.StatusCode = (int)status;
        context.Response.Close();
    }

    /// <summary>A minimal fixed-window per-source rate limit — the endpoint is
    /// authenticated and inherently low-volume (forwarded event logs, not a flood-prone
    /// raw socket), so this does not need the ingest path's token-bucket sophistication.</summary>
    private bool AllowByRate(string sourceIp)
    {
        long nowMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        MinuteBucket bucket = _rateBuckets.GetOrAdd(sourceIp, _ => new MinuteBucket());
        lock (bucket)
        {
            if (bucket.Minute != nowMinute)
            {
                bucket.Minute = nowMinute;
                bucket.Count = 0;
            }

            bucket.Count++;
            return bucket.Count <= _options.MaxRequestsPerSourcePerMinute;
        }
    }

    private sealed class MinuteBucket
    {
        public long Minute;
        public int Count;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        _logger.LogInformation("Windows Event Log intake listener {Name} stopped.", Name);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing Windows Event Log listener.");
        }

        _cts?.Dispose();
        _listener?.Close();
    }
}
