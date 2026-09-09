using System.Text;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Appends a templated line to a rotating file under the configured base directory
/// (PHASE_07 item 3 "WriteToFile"). Rotation happens by size and by age; a
/// <c>{hostname}</c> in the file name is reduced to a safe token before it is used.
/// </summary>
internal sealed class FileWriteExecutor : IActionExecutor
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (WriteToFileAction)context.Action;

        string name = SubstituteName(action.FileName, context);
        string? path = SafeFilePath.Resolve(context.Options.FileActionBaseDirectory, name, out string error);
        if (path is null)
        {
            return ActionResult.Permanent(error);
        }

        string line = FieldTemplate.Render(action.LineTemplate, context.Event).ReplaceLineEndings(" ") + Environment.NewLine;

        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotateIfNeeded(path, action);
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return ActionResult.Transient($"file write failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ActionResult.Permanent($"file write denied: {ex.Message}");
        }
        finally
        {
            WriteGate.Release();
        }

        return ActionResult.Success($"appended to {Path.GetFileName(path)}");
    }

    private static string SubstituteName(string template, ActionContext context)
    {
        if (!template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        // Render, then reduce every substituted value to a filename-safe token.
        string rendered = FieldTemplate.Render(template, context.Event);
        return SafeFilePath.Sanitize(rendered);
    }

    private static void RotateIfNeeded(string path, WriteToFileAction action)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var info = new FileInfo(path);
        bool tooBig = action.RotateAtBytes > 0 && info.Length >= action.RotateAtBytes;
        bool tooOld = action.RotateAfterDays > 0
            && DateTime.UtcNow - info.CreationTimeUtc > TimeSpan.FromDays(action.RotateAfterDays);

        if (!tooBig && !tooOld)
        {
            return;
        }

        string stamped = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, stamped, overwrite: false);
        }
        catch (IOException)
        {
            // another writer rotated it first — fine
        }
    }
}
