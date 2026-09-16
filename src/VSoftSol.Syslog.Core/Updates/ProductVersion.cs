namespace VSoftSol.Syslog.Core.Updates;

/// <summary>
/// A minimal, dependency-free comparer for this product's dotted-triple version strings
/// (v1.1 — ADR 0021). <see cref="System.Version"/> alone is insufficient: release tags are
/// <c>v1.2.0</c>-style (an optional leading <c>v</c>), and a malformed value must never
/// throw — a corrupt or unparseable version is simply "not newer," never a crash
/// (SECURITY_STANDARDS.md: fail closed).
/// </summary>
public static class ProductVersion
{
    /// <summary>Parses an optional leading 'v'/'V' plus Major.Minor.Patch. Ignores any
    /// trailing '-suffix' (e.g. a pre-release tag) for comparison purposes.</summary>
    public static bool TryParse(string? version, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string trimmed = version.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        int dashIndex = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            trimmed = trimmed[..dashIndex];
        }

        string[] parts = trimmed.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out int major) || major < 0
            || !int.TryParse(parts[1], out int minor) || minor < 0
            || !int.TryParse(parts[2], out int patch) || patch < 0)
        {
            return false;
        }

        parsed = (major, minor, patch);
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> parses and is strictly greater than
    /// <paramref name="current"/>. Never throws; an unparseable candidate is never "newer."</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out (int Major, int Minor, int Patch) c))
        {
            return false;
        }

        if (!TryParse(current, out (int Major, int Minor, int Patch) r))
        {
            // The running product's own version is always well-formed in practice; if it
            // somehow is not, err toward "not newer" rather than offering an update against
            // an unknown baseline.
            return false;
        }

        if (c.Major != r.Major)
        {
            return c.Major > r.Major;
        }

        if (c.Minor != r.Minor)
        {
            return c.Minor > r.Minor;
        }

        return c.Patch > r.Patch;
    }
}
