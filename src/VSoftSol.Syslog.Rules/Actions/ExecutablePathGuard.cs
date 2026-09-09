namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Command-execution defence for the <c>RunScript</c> action (PHASE_07 Security Validation).
/// The executable must be an absolute path with no <c>..</c>, must resolve (following any
/// symlink / reparse point) to a real file under one of the operator-configured allow-list
/// directories, and must not itself be a symlink pointing outside that tree. Arguments are
/// always passed as an argument vector by the executor — never through a shell — so this
/// guard only vets the program path.
/// </summary>
public static class ExecutablePathGuard
{
    /// <summary>Returns the resolved absolute path, or null with a reason.</summary>
    public static string? Resolve(string? path, IReadOnlyList<string> allowListDirectories, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "the executable path is empty";
            return null;
        }

        if (allowListDirectories.Count == 0)
        {
            error = "no script directories are allow-listed";
            return null;
        }

        if (path.Contains("..", StringComparison.Ordinal)
            || path.IndexOfAny(['\0', '\r', '\n']) >= 0
            || !Path.IsPathFullyQualified(path))
        {
            error = $"'{path}' is not an absolute path (or contains '..')";
            return null;
        }

        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            error = $"'{full}' does not exist";
            return null;
        }

        // Follow a symlink / reparse point to its real target and vet THAT.
        string real = full;
        try
        {
            FileSystemInfo? target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                real = Path.GetFullPath(target.FullName);
            }
        }
        catch (IOException)
        {
            // not a link, or cannot be resolved — fall through with `full`
        }

        bool underAllowed = allowListDirectories
            .Select(Path.GetFullPath)
            .Any(dir =>
            {
                string withSep = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
                return real.StartsWith(withSep, StringComparison.OrdinalIgnoreCase)
                    && full.StartsWith(withSep, StringComparison.OrdinalIgnoreCase);
            });

        if (!underAllowed)
        {
            error = $"'{path}' resolves to '{real}', which is not under an allow-listed script directory";
            return null;
        }

        return real;
    }
}
