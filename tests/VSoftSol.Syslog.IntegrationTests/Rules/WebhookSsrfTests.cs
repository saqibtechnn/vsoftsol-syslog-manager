using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 Security Validation — SSRF matrix on the webhook action. Every private,
/// loopback, link-local, or metadata target is refused <b>before</b> a request is made,
/// unless the operator has explicitly allow-listed that CIDR.
/// </summary>
public sealed class WebhookSsrfTests
{
    private static readonly ActionExecutorRegistry Registry = new();

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://127.0.0.1:8080/x")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]      // AWS/GCP metadata
    [InlineData("http://[fd00::1]/hook")]                          // IPv6 ULA
    [InlineData("http://[::1]/hook")]                              // IPv6 loopback
    [InlineData("http://10.1.2.3/hook")]
    [InlineData("http://172.16.9.9/hook")]
    [InlineData("http://192.168.1.1/hook")]
    [InlineData("http://100.64.0.1/hook")]                          // CGNAT
    public async Task Webhook_PrivateAndMetadataTargets_AreRefused(string url)
    {
        var action = new HttpWebhookAction { Url = url };
        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Detail.Should().Contain("refused");
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("gopher://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    public async Task Webhook_NonHttpSchemes_AreRefused(string url)
    {
        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(new HttpWebhookAction { Url = url }), CancellationToken.None);
        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Webhook_PrivateTarget_IsAllowedOnlyWithAnExplicitCidrOptIn()
    {
        string? seen = null;
        await using var sink = new HttpSink(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
            seen = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
        });

        var action = new HttpWebhookAction { Url = sink.Url, AllowPrivateNetwork = true, BodyTemplate = "{{}}" };
        var options = new ActionExecutorOptions { WebhookPrivateNetworkAllowList = ["127.0.0.0/8"] };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeTrue(because: result.Detail);
        seen.Should().Be("{}");

        // Same action, opt-in flag set but the CIDR NOT allow-listed → still refused.
        var noCidr = new ActionExecutorOptions();
        (await Registry.ExecuteAsync(ActionTestSupport.Context(action, options: noCidr), CancellationToken.None))
            .Ok.Should().BeFalse();
    }
}
