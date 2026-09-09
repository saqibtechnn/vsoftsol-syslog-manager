using System.Net;
using System.Net.Http;
using System.Text;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// POSTs a templated JSON body to an HTTP(S) endpoint (PHASE_07 item 3 "HttpWebhook").
/// SSRF-hardened: scheme allow-list, <see cref="PrivateNetworkGuard"/> on every resolved
/// address, redirects disabled, per-request timeout, and a bounded response read. CR/LF is
/// stripped from templated header values. Retry/back-off is the dispatcher's job.
/// </summary>
internal sealed class WebhookExecutor : IActionExecutor
{
    // One handler for the process. Redirects OFF is the SSRF-critical setting.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.None,
    });

    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (HttpWebhookAction)context.Action;

        if (!Uri.TryCreate(action.Url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ActionResult.Permanent("the webhook URL is not an absolute http/https URL");
        }

        IReadOnlyList<string> allow = action.AllowPrivateNetwork
            ? context.Options.WebhookPrivateNetworkAllowList
            : [];
        string? blocked = await PrivateNetworkGuard.CheckAsync(uri.Host, allow, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return ActionResult.Permanent($"webhook refused: {blocked}");
        }

        int timeout = Math.Clamp(action.TimeoutSeconds, 1, context.Options.MaxActionTimeoutSeconds);
        using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perRequest.CancelAfter(TimeSpan.FromSeconds(timeout));

        string body = FieldTemplate.Render(action.BodyTemplate, context.Event);
        using var request = new HttpRequestMessage(new HttpMethod(action.Method.ToUpperInvariant()), uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        foreach ((string key, string value) in action.Headers)
        {
            string safe = FieldTemplate.Render(value, context.Event).ReplaceLineEndings(string.Empty);
            request.Headers.TryAddWithoutValidation(key, safe);
        }

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, perRequest.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Transient($"webhook timed out after {timeout}s");
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Transient($"webhook request failed: {ex.Message}");
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                return ActionResult.Permanent($"webhook returned a redirect ({status}); redirects are disabled");
            }

            // Read at most a small slice of the body for the audit detail.
            string snippet = await ReadCappedAsync(response, context.Options.MaxCapturedBytes, perRequest.Token)
                .ConfigureAwait(false);

            if (status is >= 200 and < 300)
            {
                return ActionResult.Success($"webhook {status}");
            }

            string detail = $"webhook {status}: {snippet}";
            return status is >= 500 or 429 or 408 ? ActionResult.Transient(detail) : ActionResult.Permanent(detail);
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int cap, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[Math.Min(cap, 4096)];
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(buffer, 0, read).ReplaceLineEndings(" ");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return "(response body unreadable)";
        }
    }
}
