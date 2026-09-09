namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Path-traversal defence for the <c>WriteToFile</c> action (PHASE_07 Security Validation):
/// <c>../</c>, UNC paths, alternate data streams (<c>name:stream</c>), reserved Windows
/// device names, absolute paths, and separators that arrive through a <c>{hostname}</c>
/// substitution are all refused. The final resolved path must stay under the configured
/// base directory.
/// </summary>
public static class SafeFilePath
{
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Resolves <paramref name="relativeName"/> under <paramref name="baseDirectory"/>.
    /// Returns null and fills <paramref name="error"/> on any unsafe shape.
    /// </summary>
    public static string? Resolve(string? baseDirectory, string? relativeName, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            error = "file writes are not configured (no base directory)";
            return null;
        }

        if (string.IsNullOrWhiteSpace(relativeName))
        {
            error = "the file name is empty";
            return null;
        }

        if (relativeName.Contains("..", StringComparison.Ordinal)
            || relativeName.Contains(':', StringComparison.Ordinal)
            || relativeName.StartsWith('/') || relativeName.StartsWith('\\')
            || relativeName.Contains("\\\\", StringComparison.Ordinal)
            || relativeName.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            error = $"the file name '{relativeName}' is not a safe relative path";
            return null;
        }

        foreach (string segment in relativeName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = Path.GetFileNameWithoutExtension(segment).ToUpperInvariant();
            if (Reserved.Contains(stem))
            {
                error = $"the file name segment '{segment}' is a reserved device name";
                return null;
            }
        }

        string root = Path.GetFullPath(baseDirectory);
        string full = Path.GetFullPath(Path.Combine(root, relativeName));

        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            error = "the resolved path escapes the base directory";
            return null;
        }

        return full;
    }

    /// <summary>
    /// Reduces a substituted value to a filename-safe token: letters, digits, and single
    /// <c>_ -</c> or <c>.</c> separators. Runs of dots (which could form <c>..</c>) and
    /// leading dots are collapsed.
    /// </summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 128)];
        int n = 0;
        char previous = '\0';
        foreach (char c in value)
        {
            if (n == buffer.Length)
            {
                break;
            }

            char mapped = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_';

            // no two dots in a row, and no leading dot
            if (mapped == '.' && (previous == '.' || n == 0))
            {
                mapped = '_';
            }

            buffer[n++] = mapped;
            previous = mapped;
        }

        string result = new(buffer[..n]);
        return result.Length == 0 || result.All(ch => ch is '_' or '-') ? "unknown" + result : result;
    }
}
