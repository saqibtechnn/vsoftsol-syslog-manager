using System.Net;
using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 fault injection: every action type's failure path is contained, classified
/// (transient vs permanent), and never throws past the executor. Success paths verify
/// against the sink's own record.
/// </summary>
public sealed class ActionExecutorTests
{
    private static readonly ActionExecutorRegistry Registry = new();

    // ---- Email --------------------------------------------------------------

    [Fact]
    public async Task Email_Success_DeliversToTheSink()
    {
        await using var smtp = new SmtpSink(SmtpBehaviour.Accept);
        var action = new SendEmailAction
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            UseTls = false,
            From = "alerts@example.com",
            To = ["ops@example.com"],
            Subject = "{severity}: {hostname}",
            Body = "{message}",
        };

        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action), CancellationToken.None);

        result.Ok.Should().BeTrue(because: result.Detail);
        smtp.ReceivedData.Should().ContainSingle();
        smtp.ReceivedData[0].Should().Contain("disk full on /var");
    }

    [Fact]
    public async Task Email_SubjectCrLfInjection_CannotAddHeadersOrRecipients()
    {
        await using var smtp = new SmtpSink(SmtpBehaviour.Accept);
        var action = new SendEmailAction
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            UseTls = false,
            From = "alerts@example.com",
            To = ["ops@example.com"],
            Subject = "{hostname}",
            Body = "b",
        };
        var evt = ActionTestSupport.Event(hostname: "pwn\r\nBcc: attacker@evil.com\r\nX-Injected: 1");

        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action, evt), CancellationToken.None);

        result.Ok.Should().BeTrue();
        string received = smtp.ReceivedData.Single();
        // The payload is flattened onto the Subject line — it must NOT become its own header
        // line, and no extra recipient may appear.
        received.Should().NotContain("\nBcc:").And.NotContain("\nX-Injected:");
        received.Should().MatchRegex(@"(?m)^Subject: pwn");
        received.Should().NotMatchRegex(@"(?m)^To: .*attacker@evil\.com");
    }

    [Fact]
    public async Task Email_ServerRefusesConnection_IsTransient()
    {
        var action = new SendEmailAction
        {
            Host = "127.0.0.1",
            Port = 9,
            UseTls = false, // discard port, nothing listening
            From = "a@example.com",
            To = ["b@example.com"],
            Subject = "s",
            Body = "b",
        };

        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Email_Server5xx_IsPermanent()
    {
        await using var smtp = new SmtpSink(SmtpBehaviour.Reject5xx);
        var action = new SendEmailAction
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            UseTls = false,
            From = "a@example.com",
            To = ["b@example.com"],
            Subject = "s",
            Body = "b",
        };

        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    // ---- Webhook ------------------------------------------------------------

    [Fact]
    public async Task Webhook_Success_PostsTheRenderedBody()
    {
        string? seen = null;
        await using var sink = new HttpSink(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            seen = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 202;
        });

        (HttpWebhookAction action, ActionExecutorOptions options) =
            ActionTestSupport.LoopbackWebhook(sink.Url, "{{\"m\":\"{message}\"}}");
        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeTrue(because: result.Detail);
        seen.Should().Be("{\"m\":\"disk full on /var\"}");
    }

    [Fact]
    public async Task Webhook_500_IsTransient_400_IsPermanent()
    {
        await using var five = new HttpSink(ctx => { ctx.Response.StatusCode = 500; return Task.CompletedTask; });
        (HttpWebhookAction a5, ActionExecutorOptions o5) = ActionTestSupport.LoopbackWebhook(five.Url);
        (await Registry.ExecuteAsync(ActionTestSupport.Context(a5, options: o5), CancellationToken.None))
            .Retryable.Should().BeTrue();

        await using var four = new HttpSink(ctx => { ctx.Response.StatusCode = 400; return Task.CompletedTask; });
        (HttpWebhookAction a4, ActionExecutorOptions o4) = ActionTestSupport.LoopbackWebhook(four.Url);
        ActionResult permanent = await Registry.ExecuteAsync(
            ActionTestSupport.Context(a4, options: o4), CancellationToken.None);
        permanent.Ok.Should().BeFalse();
        permanent.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Webhook_Timeout_IsTransient_AndDoesNotHangTheCaller()
    {
        await using var slow = new HttpSink(async ctx =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            ctx.Response.StatusCode = 200;
        });

        (HttpWebhookAction action, ActionExecutorOptions options) =
            ActionTestSupport.LoopbackWebhook(slow.Url, timeoutSeconds: 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);
        sw.Stop();

        result.Retryable.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Webhook_Redirect_IsRefused_NotFollowed()
    {
        await using var sink = new HttpSink(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "http://169.254.169.254/latest/meta-data/";
            return Task.CompletedTask;
        });

        (HttpWebhookAction action, ActionExecutorOptions options) = ActionTestSupport.LoopbackWebhook(sink.Url);
        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("redirect");
    }

    // ---- Notification -----------------------------------------------------

    [Fact]
    public async Task Notification_RaisesViaTheSink()
    {
        (NotificationLevel Level, string Title, string Body)? raised = null;
        NotificationSink sink = (level, title, body, _, _) =>
        {
            raised = (level, title, body);
            return ValueTask.CompletedTask;
        };

        var action = new RaiseNotificationAction { Level = NotificationLevel.Critical, Title = "{hostname} down", Body = "{message}" };
        await Registry.ExecuteAsync(ActionTestSupport.Context(action, notifications: sink), CancellationToken.None);

        raised.Should().NotBeNull();
        raised!.Value.Title.Should().Be("core-sw-1 down");
        raised.Value.Level.Should().Be(NotificationLevel.Critical);
    }

    // ---- Forward ---------------------------------------------------------

    [Fact]
    public async Task Forward_Udp_DeliversTheRawBytes()
    {
        using var sink = new UdpSink();
        var action = new ForwardSyslogAction { Host = "127.0.0.1", Port = sink.Port, Transport = ForwardTransport.Udp };

        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action), CancellationToken.None);
        byte[] got = await sink.ReceiveAsync(TimeSpan.FromSeconds(5));

        result.Ok.Should().BeTrue();
        Encoding.UTF8.GetString(got).Should().Be("disk full on /var");
    }

    [Fact]
    public async Task Forward_ToOwnListener_IsRefusedAsALoop_NeverSent()
    {
        using var sink = new UdpSink();
        var options = new ActionExecutorOptions { LocalSyslogEndpoints = [$"127.0.0.1:{sink.Port}"] };
        var action = new ForwardSyslogAction { Host = "127.0.0.1", Port = sink.Port, Transport = ForwardTransport.Udp };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("loop");
        Func<Task> nothingArrived = () => sink.ReceiveAsync(TimeSpan.FromMilliseconds(400));
        await nothingArrived.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- File ----------------------------------------------------------

    [Fact]
    public async Task File_Append_ThenRotateBySize()
    {
        string baseDir = NewTempDir();
        var options = new ActionExecutorOptions { FileActionBaseDirectory = baseDir };
        var action = new WriteToFileAction { FileName = "out.log", LineTemplate = "{message}", RotateAtBytes = 40 };

        for (int i = 0; i < 6; i++)
        {
            (await Registry.ExecuteAsync(ActionTestSupport.Context(action, options: options), CancellationToken.None))
                .Ok.Should().BeTrue();
        }

        Directory.GetFiles(baseDir, "out.log*").Length.Should().BeGreaterThan(1, "the file rotated");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\drivers\\etc\\hosts")]
    [InlineData("C:\\Windows\\Temp\\evil.log")]
    [InlineData("\\\\server\\share\\evil.log")]
    [InlineData("out.log:hidden")]
    [InlineData("CON")]
    public async Task File_PathTraversalAndTricks_AreRefused(string fileName)
    {
        var options = new ActionExecutorOptions { FileActionBaseDirectory = NewTempDir() };
        var action = new WriteToFileAction { FileName = fileName, LineTemplate = "x" };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task File_HostnameWithSeparators_IsSanitised_StaysUnderBase()
    {
        string baseDir = NewTempDir();
        var options = new ActionExecutorOptions { FileActionBaseDirectory = baseDir };
        var action = new WriteToFileAction { FileName = "{hostname}.log", LineTemplate = "x" };
        var evt = ActionTestSupport.Event(hostname: "../../../../etc/cron.d/evil");

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, evt, options), CancellationToken.None);

        result.Ok.Should().BeTrue(because: result.Detail);
        Directory.GetFiles(baseDir, "*.log").Should().ContainSingle();
        Directory.GetFiles(baseDir).Single().Should().NotContain("..");
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-fileaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
