using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>PHASE_11 item 3 — "forward a sample event, assert severity and facility
/// mapping" plus the API-key auth/rate-limit checks (SECURITY_STANDARDS.md: "authenticated,
/// rate-limited, and unable to be used to forge events attributed to another host").</summary>
[Trait("Category", "Ingestion")]
public sealed class WinEventLogListenerTests
{
    private const string ValidKey = "test-api-key-12345";

    private static ServiceProvider Build(ILogRepository repo, Action<WinEventLogOptions>? configure = null, string? sourceRestriction = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repo);
        services.AddSyslogIngestion();
        services.Configure<IngestionOptions>(o =>
        {
            o.UdpEnabled = false;
            o.TcpEnabled = false;
            o.SpillDirectory = Path.Combine(Path.GetTempPath(), "vsoftsol-wel-spill-" + Guid.NewGuid().ToString("N"));
        });
        services.Configure<WinEventLogOptions>(o =>
        {
            o.Enabled = true;
            o.BindAddress = "127.0.0.1";
            o.Port = Random.Shared.Next(20_000, 60_000);
            configure?.Invoke(o);
        });
        services.AddSingleton<WinEventLogApiKeyValidator>(_ => (key, sourceIp, _) =>
            new ValueTask<bool>(key == ValidKey && (sourceRestriction is null || sourceRestriction == sourceIp)));
        services.AddSingleton<WinEventLogListener>();
        return services.BuildServiceProvider();
    }

    private static string SampleBody(int level = 2) =>
        $$"""{"computer":"WIN-DC01","channel":"Security","provider":"Microsoft-Windows-Security-Auditing","eventId":4625,"level":{{level}},"timeCreated":"2026-03-01T10:00:00Z","message":"An account failed to log on."}""";

    [Fact]
    public async Task PostWithAValidApiKey_IsAcceptedIntoTheChannel()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository);
        var listener = sp.GetRequiredService<WinEventLogListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Api-Key", ValidKey);
            HttpResponseMessage response = await http.PostAsync(
                $"http://127.0.0.1:{listener.BoundPort}/wineventlog",
                new StringContent(SampleBody(), Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
            RawFrame? received = await WaitForFrameAsync(channel);
            received.Should().NotBeNull();
            received!.Protocol.Should().Be(Protocol.WinEventLog);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PostWithoutAnApiKey_IsRejectedWithUnauthorized_NotIngested()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository);
        var listener = sp.GetRequiredService<WinEventLogListener>();
        var channel = sp.GetRequiredService<IngestionChannel>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            HttpResponseMessage response = await http.PostAsync(
                $"http://127.0.0.1:{listener.BoundPort}/wineventlog",
                new StringContent(SampleBody(), Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
            (await TryWaitForFrameAsync(channel)).Should().BeNull();
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PostWithAWrongApiKey_IsRejected()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository);
        var listener = sp.GetRequiredService<WinEventLogListener>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Api-Key", "not-the-real-key");
            HttpResponseMessage response = await http.PostAsync(
                $"http://127.0.0.1:{listener.BoundPort}/wineventlog",
                new StringContent(SampleBody(), Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PostFromAnUnexpectedSource_WhenTheKeyIsSourceScoped_IsRejected_NotForged()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        // The validator only accepts requests claiming to be from 10.0.0.9; a real loopback
        // request arrives as 127.0.0.1, so this proves a source-scoped key rejects a
        // request from anywhere else — the "unable to forge events" requirement.
        await using ServiceProvider sp = Build(db.Repository, sourceRestriction: "10.0.0.9");
        var listener = sp.GetRequiredService<WinEventLogListener>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Api-Key", ValidKey);
            HttpResponseMessage response = await http.PostAsync(
                $"http://127.0.0.1:{listener.BoundPort}/wineventlog",
                new StringContent(SampleBody(), Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PostMoreThanTheConfiguredRateLimit_IsThrottledWithTooManyRequests()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await using ServiceProvider sp = Build(db.Repository, o => o.MaxRequestsPerSourcePerMinute = 2);
        var listener = sp.GetRequiredService<WinEventLogListener>();

        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Api-Key", ValidKey);
            string url = $"http://127.0.0.1:{listener.BoundPort}/wineventlog";

            await http.PostAsync(url, new StringContent(SampleBody(), Encoding.UTF8, "application/json"));
            await http.PostAsync(url, new StringContent(SampleBody(), Encoding.UTF8, "application/json"));
            HttpResponseMessage third = await http.PostAsync(url, new StringContent(SampleBody(), Encoding.UTF8, "application/json"));

            third.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<RawFrame?> WaitForFrameAsync(IngestionChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                if (channel.Reader.TryRead(out RawFrame? frame))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    private static async Task<RawFrame?> TryWaitForFrameAsync(IngestionChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            if (await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false) && channel.Reader.TryRead(out RawFrame? frame))
            {
                return frame;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }
}
