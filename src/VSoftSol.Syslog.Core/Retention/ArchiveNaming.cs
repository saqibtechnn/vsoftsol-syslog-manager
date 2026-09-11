using System.Globalization;
using System.Text;

namespace VSoftSol.Syslog.Core.Retention;

/// <summary>
/// Pure, no-I/O path construction for archive files (PHASE_10 build item 4; SECURITY_STANDARDS.md
/// "Path traversal | Hostnames and app-names ... may reach file paths in ... archive naming").
/// Stream names are administrator-authored, not raw network input, but are still sanitised
/// defensively — never trust a display string to be filesystem-safe. Every candidate path is
/// re-verified with <see cref="IsSafeUnderRoot"/> immediately before a file is opened, at both
/// write and read time, so a traversal segment can never escape the configured archive root.
/// </summary>
public static class ArchiveNaming
{
    private static readonly char[] ForbiddenChars =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n'];

    private const int MaxSegmentLength = 80;

    /// <summary>
    /// Strips path separators, drive letters, reserved characters, and any leading dots
    /// (which would otherwise let <c>..</c> or a hidden-file collide) from a name destined
    /// to become part of a file path. Falls back to "unnamed" when nothing safe remains.
    /// </summary>
    public static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxSegmentLength));
        foreach (char c in value)
        {
            if (Array.IndexOf(ForbiddenChars, c) >= 0 || char.IsControl(c))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(c);
            }

            if (builder.Length >= MaxSegmentLength)
            {
                break;
            }
        }

        string cleaned = builder.ToString().Trim().TrimStart('.');
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    /// <summary>Builds the deterministic archive file name for one stream/period export.
    /// Deterministic so a re-run after an interrupted tiering pass regenerates the same
    /// file (byte-identical content -&gt; same hash) instead of creating a duplicate.</summary>
    public static string BuildFileName(string streamName, DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtc)
    {
        string safeName = SanitizeSegment(streamName);
        string start = periodStartUtc.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
        string end = periodEndUtc.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
        return $"{safeName}_{start}_{end}.vsarc";
    }

    /// <summary>
    /// True when <paramref name="candidateFullPath"/> resolves to a location at or beneath
    /// <paramref name="rootFullPath"/>. Both are expected to already be
    /// <see cref="Path.GetFullPath(string)"/>-resolved by the caller (this method does not
    /// touch the filesystem, keeping it pure and unit-testable); comparison is
    /// case-insensitive and separator-normalised, matching Windows path semantics.
    /// </summary>
    public static bool IsSafeUnderRoot(string rootFullPath, string candidateFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateFullPath);

        string root = NormalizeForComparison(rootFullPath);
        string candidate = NormalizeForComparison(candidateFullPath);

        if (!root.EndsWith('/'))
        {
            root += "/";
        }

        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && candidate.Length > root.Length;
    }

    private static string NormalizeForComparison(string path) =>
        path.Replace('\\', '/').TrimEnd('/');
}
