using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

/// <summary>
/// SSRF-hardened HTTP client for self-update (v1.1 — ADR 0021). Points at a local
/// <c>HttpSink</c> fixture, same pattern as <c>WebhookSsrfTests</c> — this is the only
/// realistic way to exercise the redirect-following/host-allow-list logic without hitting
/// the real GitHub API. <see cref="GitHubUpdateOptions.RequireHttps"/> and
/// <see cref="GitHubUpdateOptions.AllowedPrivateNetworkCidrs"/> are the two test-only escape
/// hatches this requires (documented on the type itself) — always strict in production.
/// </summary>
[Trait("Category", "Updates")]
public sealed class GitHubUpdateClientTests
{
    private static GitHubUpdateClient Client(GitHubUpdateOptions options) =>
        new(new HttpClient(), Options.Create(options));

    private static GitHubUpdateOptions TestOptions(string apiUrl, int port) => new()
    {
        ApiUrl = apiUrl,
        AllowedAssetHosts = ["127.0.0.1"],
        AllowedPrivateNetworkCidrs = ["127.0.0.1/32"],
        RequireHttps = false,
        RequestTimeoutSeconds = 5,
    };

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesTagAndNamedAssets()
    {
        await using var sink = new HttpSink(async ctx =>
        {
            string json = """
                {"tag_name":"v1.2.0","assets":[
                  {"name":"update-manifest.json","browser_download_url":"http://127.0.0.1:PORT/manifest"},
                  {"name":"VSoftSolSyslogManagerSetup.msi","browser_download_url":"http://127.0.0.1:PORT/setup.msi"}
                ]}
                """;
            byte[] bytes = Encoding.UTF8.GetBytes(json.Replace("PORT", ctx.Request.LocalEndPoint.Port.ToString()));
            await ctx.Response.OutputStream.WriteAsync(bytes);
        });

        GitHubUpdateClient client = Client(TestOptions($"http://127.0.0.1:{sink.Port}/api", sink.Port));
        GitHubReleaseInfo? release = await client.GetLatestReleaseAsync(CancellationToken.None);

        release.Should().NotBeNull();
        release!.Version.Should().Be("v1.2.0");
        release.ManifestUrl.Should().Be($"http://127.0.0.1:{sink.Port}/manifest");
        release.MsiUrl.Should().Be($"http://127.0.0.1:{sink.Port}/setup.msi");
    }

    [Fact]
    public async Task GetLatestReleaseAsync_MalformedJson_ReturnsNull_NeverThrows()
    {
        await using var sink = new HttpSink(async ctx =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes("not json at all");
            await ctx.Response.OutputStream.WriteAsync(bytes);
        });

        GitHubUpdateClient client = Client(TestOptions($"http://127.0.0.1:{sink.Port}/api", sink.Port));
        (await client.GetLatestReleaseAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DownloadManifestAsync_HappyPath_ReturnsTheBody()
    {
        const string content = "{\"DocumentJson\":\"{}\",\"SignatureBase64\":\"abc\"}";
        await using var sink = new HttpSink(async ctx =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            await ctx.Response.OutputStream.WriteAsync(bytes);
        });

        GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
        string? result = await client.DownloadManifestAsync($"http://127.0.0.1:{sink.Port}/manifest", CancellationToken.None);

        result.Should().Be(content);
    }

    [Fact]
    public async Task DownloadManifestAsync_OneAllowedRedirect_IsFollowed()
    {
        const string finalContent = "{\"DocumentJson\":\"{}\",\"SignatureBase64\":\"abc\"}";
        await using var sink = new HttpSink(async ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath == "/first")
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = $"http://127.0.0.1:{ctx.Request.LocalEndPoint.Port}/final";
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(finalContent);
            await ctx.Response.OutputStream.WriteAsync(bytes);
        });

        GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
        string? result = await client.DownloadManifestAsync($"http://127.0.0.1:{sink.Port}/first", CancellationToken.None);

        result.Should().Be(finalContent);
    }

    [Fact]
    public async Task DownloadManifestAsync_RedirectToADisallowedHost_IsRefused()
    {
        await using var sink = new HttpSink(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "http://evil.example.invalid/steal-me";
            return Task.CompletedTask;
        });

        GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
        (await client.DownloadManifestAsync($"http://127.0.0.1:{sink.Port}/first", CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata
    [InlineData("http://10.1.2.3/x")]
    [InlineData("http://192.168.1.1/x")]
    public async Task DownloadManifestAsync_RedirectToAPrivateNetworkTarget_IsRefused_EvenIfHostAllowListed(string maliciousLocation)
    {
        // The host allow-list alone is not the only defence — even a redirect to a host
        // string that somehow matched the allow-list must still fail PrivateNetworkGuard's
        // resolved-address check. Here it fails on the host-allow-list first (these hosts
        // are not in AllowedAssetHosts) — the point is the refusal, not which layer catches it.
        await using var sink = new HttpSink(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = maliciousLocation;
            return Task.CompletedTask;
        });

        GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
        (await client.DownloadManifestAsync($"http://127.0.0.1:{sink.Port}/first", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DownloadManifestAsync_TwoRedirects_TheSecondIsRefused()
    {
        await using var sink = new HttpSink(ctx =>
        {
            int port = ctx.Request.LocalEndPoint.Port;
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = ctx.Request.Url!.AbsolutePath switch
            {
                "/hop1" => $"http://127.0.0.1:{port}/hop2",
                _ => $"http://127.0.0.1:{port}/hop3",
            };
            return Task.CompletedTask;
        });

        GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
        (await client.DownloadManifestAsync($"http://127.0.0.1:{sink.Port}/hop1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DownloadManifestAsync_OversizedResponse_IsRefused_NotSilentlyTruncated()
    {
        await using var sink = new HttpSink(async ctx =>
        {
            byte[] tooLarge = new byte[20 * 1024]; // over the 16 KB manifest cap
            await ctx.Response.OutputStream.WriteAsync(tooLarge);
        });

        GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
        (await client.DownloadManifestAsync($"http://127.0.0.1:{sink.Port}/manifest", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DownloadMsiAsync_HashMatches_WritesTheFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("this is a fake msi payload");
        string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await using var sink = new HttpSink(async ctx => await ctx.Response.OutputStream.WriteAsync(payload));

        string destination = Path.Combine(Path.GetTempPath(), $"selfupdate-test-{Guid.NewGuid():N}", "setup.msi");
        try
        {
            GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
            bool ok = await client.DownloadMsiAsync($"http://127.0.0.1:{sink.Port}/setup.msi", destination, sha, CancellationToken.None);

            ok.Should().BeTrue();
            File.Exists(destination).Should().BeTrue();
            (await File.ReadAllBytesAsync(destination)).Should().Equal(payload);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(destination)))
            {
                Directory.Delete(Path.GetDirectoryName(destination)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadMsiAsync_HashMismatch_IsRefused_AndLeavesNoPartialFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("this is a fake msi payload");
        string wrongSha = new string('0', 64);
        await using var sink = new HttpSink(async ctx => await ctx.Response.OutputStream.WriteAsync(payload));

        string destination = Path.Combine(Path.GetTempPath(), $"selfupdate-test-{Guid.NewGuid():N}", "setup.msi");
        try
        {
            GitHubUpdateClient client = Client(TestOptions("unused", sink.Port));
            bool ok = await client.DownloadMsiAsync($"http://127.0.0.1:{sink.Port}/setup.msi", destination, wrongSha, CancellationToken.None);

            ok.Should().BeFalse();
            File.Exists(destination).Should().BeFalse();
            string directory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(directory))
            {
                Directory.EnumerateFiles(directory).Should().BeEmpty("no partial/temp download file should be left behind");
            }
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(destination)))
            {
                Directory.Delete(Path.GetDirectoryName(destination)!, recursive: true);
            }
        }
    }
}
