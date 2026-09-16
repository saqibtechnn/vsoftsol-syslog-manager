using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Data.Updates;

/// <summary>The latest release GitHub reports, with the two asset URLs this client cares
/// about (either may be null if the release has no matching asset).</summary>
public sealed record GitHubReleaseInfo(string Version, string? ManifestUrl, string? MsiUrl);

/// <summary>
/// SSRF-hardened GitHub Releases client for self-update (v1.1 — ADR 0021). Mirrors
/// <c>WebhookExecutor</c>'s conventions (redirects off by default, connect timeout, capped
/// reads, <see cref="PrivateNetworkGuard"/> — a local, deliberately-duplicated copy of the
/// Rules-layer guard of the same name; see that type's doc comment) with one deliberate
/// addition: GitHub Releases
/// assets are served via exactly one redirect to a CDN, so — unlike the webhook action,
/// which refuses every redirect outright — a manifest/MSI download follows at most one hop,
/// re-validating the resolved <c>Location</c>'s host against a small fixed allow-list and
/// its resolved address against the same private-network guard, before the second request.
/// The GitHub API call itself never redirects for this endpoint and needs no exception.
/// </summary>
public sealed class GitHubUpdateClient
{
    private const int MaxApiResponseBytes = 64 * 1024;
    private const int MaxManifestBytes = 16 * 1024;
    private const int MaxMsiBytes = 200 * 1024 * 1024; // generous; the real installer is ~12 MB

    private readonly HttpClient _http;
    private readonly GitHubUpdateOptions _options;

    public GitHubUpdateClient(HttpClient http, IOptions<GitHubUpdateOptions>? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? new GitHubUpdateOptions();
        _http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
    }

    /// <summary>GETs the fixed, configured release-list endpoint. Returns null on any
    /// failure (unreachable, non-2xx, an unexpected redirect, malformed JSON, or a release
    /// missing a parseable <c>tag_name</c>) — never throws.</summary>
    public async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.ApiUrl);
        request.Headers.UserAgent.ParseAdd("VSoftSol-Syslog-Manager-SelfUpdate/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        using (response)
        {
            // The releases/latest endpoint never redirects; treat one as a hard failure
            // rather than silently following it, matching WebhookExecutor's discipline.
            if ((int)response.StatusCode is >= 300 and < 400 || !response.IsSuccessStatusCode)
            {
                return null;
            }

            (bool truncated, byte[] bytes) = await ReadCappedAsync(response, MaxApiResponseBytes, cancellationToken).ConfigureAwait(false);
            return truncated ? null : ParseRelease(bytes);
        }
    }

    private GitHubReleaseInfo? ParseRelease(byte[] jsonBytes)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            string? manifestUrl = null;
            string? msiUrl = null;
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string name = nameEl.GetString() ?? string.Empty;
                    string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) && urlEl.ValueKind == JsonValueKind.String
                        ? urlEl.GetString()
                        : null;

                    if (url is null)
                    {
                        continue;
                    }

                    if (string.Equals(name, _options.ManifestAssetName, StringComparison.Ordinal))
                    {
                        manifestUrl = url;
                    }
                    else if (name.EndsWith(_options.MsiAssetNameSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        msiUrl = url;
                    }
                }
            }

            return new GitHubReleaseInfo(tag, manifestUrl, msiUrl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Downloads and returns the manifest's raw JSON text, or null on any failure
    /// (unreachable, oversized, a disallowed redirect target, non-2xx).</summary>
    public async Task<string?> DownloadManifestAsync(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = await SendWithAtMostOneRedirectAsync(url, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        using (response)
        {
            (bool truncated, byte[] bytes) = await ReadCappedAsync(response, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
            return truncated ? null : Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Downloads the MSI to <paramref name="destinationPath"/>, hashing incrementally while
    /// streaming, and renames it into place only if the SHA-256 matches
    /// <paramref name="expectedSha256Hex"/> (lowercase hex). On any failure — network,
    /// oversized, disallowed redirect, hash mismatch — returns false and leaves no partial
    /// file at <paramref name="destinationPath"/>.
    /// </summary>
    public async Task<bool> DownloadMsiAsync(string url, string destinationPath, string expectedSha256Hex, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = await SendWithAtMostOneRedirectAsync(url, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return false;
        }

        string tempPath = destinationPath + ".download";
        using (response)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            try
            {
                using SHA256 sha = SHA256.Create();
                long total = 0;
                await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream dest = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxMsiBytes)
                        {
                            return false;
                        }

                        sha.TransformBlock(buffer, 0, read, null, 0);
                        await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                sha.TransformFinalBlock([], 0, 0);
                string actualHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(actualHash, expectedSha256Hex, StringComparison.Ordinal))
                {
                    return false;
                }

                File.Move(tempPath, destinationPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
            {
                return false;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // best-effort cleanup only
                    }
                }
            }
        }
    }

    /// <summary>Sends one GET, validates the initial host, and — if the response is a
    /// redirect — validates the resolved <c>Location</c>'s host too before following it
    /// exactly once. Never follows a second hop. Returns null on any refusal or failure.</summary>
    private async Task<HttpResponseMessage?> SendWithAtMostOneRedirectAsync(string url, CancellationToken cancellationToken)
    {
        string? current = url;
        for (int hop = 0; hop < 2; hop++)
        {
            if (current is null || !Uri.TryCreate(current, UriKind.Absolute, out Uri? uri))
            {
                return null;
            }

            bool schemeOk = _options.RequireHttps
                ? uri.Scheme == Uri.UriSchemeHttps
                : uri.Scheme is "https" or "http";
            if (!schemeOk)
            {
                return null;
            }

            if (!_options.AllowedAssetHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            string? blocked = await PrivateNetworkGuard.CheckAsync(uri.Host, _options.AllowedPrivateNetworkCidrs, cancellationToken)
                .ConfigureAwait(false);
            if (blocked is not null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("VSoftSol-Syslog-Manager-SelfUpdate/1.0");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                string? location = response.Headers.Location?.IsAbsoluteUri == true
                    ? response.Headers.Location.ToString()
                    : null;
                response.Dispose();
                if (hop == 1 || location is null)
                {
                    // already followed one hop, or no usable Location — refuse rather than loop.
                    return null;
                }

                current = location;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }

            return response;
        }

        return null;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; <c>Truncated</c> is true when the
    /// response has more data than that — callers treat a truncated read as a hard failure,
    /// never a silently-partial value for an integrity-sensitive document.</summary>
    private static async Task<(bool Truncated, byte[] Data)> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[Math.Min(maxBytes, 8192)];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return (true, []);
                }

                buffer.Write(chunk, 0, read);
            }

            return (false, buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException)
        {
            return (true, []);
        }
    }
}
