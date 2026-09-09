using System.Diagnostics;
using System.Text;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Runs an allow-listed executable with an argument vector (PHASE_07 item 3 "RunScript").
/// Command-injection is structurally impossible: <see cref="ProcessStartInfo.ArgumentList"/>
/// is used (never a command string), <c>UseShellExecute</c> is false, the environment is
/// not inherited beyond a minimal set, and <see cref="ExecutablePathGuard"/> re-vets the
/// program path (symlink-resolved) against the allow-list. Templated arguments are
/// substituted into vector elements only. stdout/stderr are captured (capped) for audit.
/// </summary>
internal sealed class ScriptExecutor : IActionExecutor
{
    // OS variables a process needs to launch and resolve its runtime. Nothing product-,
    // account-, or secret-bearing is on this list.
    private static readonly string[] EnvironmentAllowList =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec",
        "TEMP", "TMP", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles",
        "DOTNET_ROOT", "DOTNET_ROOT(x86)", "LD_LIBRARY_PATH", "HOME",
    ];

    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (RunScriptAction)context.Action;

        string? exe = ExecutablePathGuard.Resolve(
            action.ExecutablePath, context.Options.ScriptAllowListDirectories, out string error);
        if (exe is null)
        {
            return ActionResult.Permanent($"script refused: {error}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = ResolveWorkingDirectory(action, exe),
        };

        foreach (string arg in action.Arguments)
        {
            psi.ArgumentList.Add(FieldTemplate.Render(arg, context.Event));
        }

        // Minimal environment — carry only the OS variables a process needs to start and
        // resolve its runtime; drop everything else (the service account's config, secrets
        // in env vars, custom variables) so an attacker-influenced script cannot read them.
        psi.Environment.Clear();
        foreach (string keep in EnvironmentAllowList)
        {
            if (Environment.GetEnvironmentVariable(keep) is { } value)
            {
                psi.Environment[keep] = value;
            }
        }

        // A framework-dependent .NET remediation script needs to find its runtime; if the
        // host process didn't have DOTNET_ROOT set, derive it (a path, not a secret).
        if (!psi.Environment.ContainsKey("DOTNET_ROOT") && DerivedDotnetRoot() is { } root)
        {
            psi.Environment["DOTNET_ROOT"] = root;
        }

        psi.Environment["SYSLOG_RULE"] = context.RuleName;

        int timeout = Math.Clamp(action.TimeoutSeconds, 1, context.Options.MaxActionTimeoutSeconds * 30);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        int cap = context.Options.MaxCapturedBytes;
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data, cap);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data, cap);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ActionResult.Permanent($"could not start '{exe}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return ActionResult.Transient($"script exceeded its {timeout}s timeout and was killed");
        }

        string tail = Tail(stdout, stderr);
        return process.ExitCode == 0
            ? ActionResult.Success($"script exited 0. {tail}")
            : ActionResult.Permanent($"script exited {process.ExitCode}. {tail}");
    }

    private static string? DerivedDotnetRoot()
    {
        try
        {
            // .../<root>/shared/Microsoft.NETCore.App/<version>/System.Private.CoreLib.dll
            string? versionDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            DirectoryInfo? root = new DirectoryInfo(versionDir!).Parent?.Parent?.Parent;
            return root?.Exists == true ? root.FullName : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ResolveWorkingDirectory(RunScriptAction action, string exe)
    {
        if (string.IsNullOrWhiteSpace(action.WorkingDirectory) || !Path.IsPathFullyQualified(action.WorkingDirectory)
            || action.WorkingDirectory.Contains("..", StringComparison.Ordinal))
        {
            return Path.GetDirectoryName(exe);
        }

        return Directory.Exists(action.WorkingDirectory) ? action.WorkingDirectory : Path.GetDirectoryName(exe);
    }

    private static void Append(StringBuilder sb, string? line, int cap)
    {
        if (line is null || sb.Length >= cap)
        {
            return;
        }

        sb.Append(line).Append('\n');
    }

    private static string Tail(StringBuilder stdout, StringBuilder stderr)
    {
        string o = stdout.ToString().Trim();
        string e = stderr.ToString().Trim();
        string combined = e.Length > 0 ? $"stderr: {e}" : o.Length > 0 ? $"stdout: {o}" : "no output";
        return combined.Length <= 500 ? combined : combined[..500] + "…";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }
}
