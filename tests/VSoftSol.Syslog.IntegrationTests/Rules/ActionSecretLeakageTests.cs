using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 Security Validation — secret leakage. Every action is configured with a
/// credential, every failure path is forced, and the resulting <see cref="ActionResult.Detail"/>
/// (which the dispatcher writes to the audit log) is scanned: the secret value must never
/// appear.
/// </summary>
public sealed class ActionSecretLeakageTests
{
    private const string Secret = "p@ss-w0rd-do-not-leak-1234567890";
    private static readonly ActionExecutorRegistry Registry = new();
    private static readonly SecretResolver Resolver = (_, _) => ValueTask.FromResult<string?>(Secret);

    [Fact]
    public async Task Email_AuthFailure_DoesNotLeakThePasswordInTheResultDetail()
    {
        await using var smtp = new SmtpSink(SmtpBehaviour.Reject5xx);
        var action = new SendEmailAction
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            UseTls = false,
            Username = "svc",
            SecretName = "smtp-pw",
            From = "a@example.com",
            To = ["b@example.com"],
            Subject = "s",
            Body = "b",
        };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, secrets: Resolver), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Detail.Should().NotContain(Secret);
    }

    [Fact]
    public async Task Odbc_ConnectionFailure_DoesNotLeakThePasswordAppendedToTheConnectionString()
    {
        var action = new WriteToOdbcAction
        {
            ConnectionString = "DSN=nonexistent-dsn",
            SecretName = "odbc-pw",
            TableName = "t",
            ColumnMap = { ["c"] = "{message}" },
            TimeoutSeconds = 2,
        };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, secrets: Resolver), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Detail.Should().NotContain(Secret);
        result.Detail.Should().NotContain("PWD=");
    }

    [Fact]
    public async Task MissingSecret_IsAPermanentFailure_WithAClearButSafeMessage()
    {
        SecretResolver empty = (_, _) => ValueTask.FromResult<string?>(null);
        var action = new SendEmailAction
        {
            Host = "127.0.0.1",
            Port = 25,
            Username = "svc",
            SecretName = "smtp-pw",
            From = "a@example.com",
            To = ["b@example.com"],
            Subject = "s",
            Body = "b",
        };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, secrets: empty), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Detail.Should().Contain("smtp-pw").And.NotContain(Secret);
    }
}
