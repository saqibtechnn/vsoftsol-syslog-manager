namespace VSoftSol.Syslog.Core.VendorSupport;

/// <summary>
/// Pure Markdown parser for VENDOR_SUPPORT.md's "Device configuration commands" section
/// (PHASE_12 build item 2a). Extracting the "Waiting for messages" page's copy-ready blocks
/// straight from the same document the User Guide's Device Compatibility chapter reads means
/// the two can never silently drift apart.
/// </summary>
public static class VendorSupportDocumentParser
{
    private const string SectionHeading = "## Device configuration commands";

    public static IReadOnlyList<DeviceConfigCommand> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int sectionStart = Array.FindIndex(lines, l => l.TrimEnd() == SectionHeading);
        if (sectionStart < 0)
        {
            return [];
        }

        int sectionEnd = lines.Length;
        for (int i = sectionStart + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                sectionEnd = i;
                break;
            }
        }

        List<DeviceConfigCommand> results = [];
        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (!lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            string vendorName = lines[i]["### ".Length..].Trim();

            int bodyStart = i + 1;
            int bodyEnd = sectionEnd;
            for (int j = bodyStart; j < sectionEnd; j++)
            {
                if (lines[j].StartsWith("### ", StringComparison.Ordinal))
                {
                    bodyEnd = j;
                    break;
                }
            }

            results.Add(ParseBody(vendorName, lines.AsSpan(bodyStart, bodyEnd - bodyStart)));
            i = bodyEnd - 1;
        }

        return results;
    }

    /// <summary>Reorders <paramref name="commands"/> so the given vendor names (matched
    /// case-insensitively) come first, in the order given, followed by the rest in their
    /// original order.</summary>
    public static IReadOnlyList<DeviceConfigCommand> PinnedFirst(
        IReadOnlyList<DeviceConfigCommand> commands, IReadOnlyList<string> pinnedVendorNames)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(pinnedVendorNames);

        var byName = commands.ToDictionary(c => c.VendorName, StringComparer.OrdinalIgnoreCase);
        List<DeviceConfigCommand> ordered = [];

        foreach (string pinned in pinnedVendorNames)
        {
            if (byName.TryGetValue(pinned, out DeviceConfigCommand? command))
            {
                ordered.Add(command);
            }
        }

        HashSet<string> pinnedSet = new(pinnedVendorNames, StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(commands.Where(c => !pinnedSet.Contains(c.VendorName)));
        return ordered;
    }

    private static DeviceConfigCommand ParseBody(string vendorName, ReadOnlySpan<string> bodyLines)
    {
        int start = 0;
        int end = bodyLines.Length;
        while (start < end && bodyLines[start].Trim().Length == 0)
        {
            start++;
        }

        while (end > start && bodyLines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        if (start < end && bodyLines[start].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            int fenceEnd = -1;
            for (int i = start + 1; i < end; i++)
            {
                if (bodyLines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    fenceEnd = i;
                    break;
                }
            }

            if (fenceEnd > start)
            {
                string codeText = string.Join('\n', bodyLines[(start + 1)..fenceEnd].ToArray());
                return new DeviceConfigCommand(vendorName, codeText.Trim('\n'), IsCommandBlock: true);
            }
        }

        string proseText = string.Join('\n', bodyLines[start..end].ToArray());
        return new DeviceConfigCommand(vendorName, proseText, IsCommandBlock: false);
    }
}
