using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Branding;

/// <summary>
/// CLAUDE.md Constraint 11: brand display strings, vendor strings, URLs, and colours
/// are never literals in product source. They come from <c>BrandingInfo</c> (generated)
/// or the <c>--brand-*</c> CSS custom properties.
/// </summary>
/// <remarks>
/// Interpretation note (recorded in PROGRESS.md and docs/adr/0007): the mandated
/// solution layout in CLAUDE.md fixes the assembly and namespace token
/// <c>VSoftSol.Syslog.*</c>, so this guard targets the brand <em>values</em> — the
/// product name, vendor name, URLs, support address, and hex colours — not the
/// structural namespace token.
/// </remarks>
public sealed class BrandingLiteralTests
{
    private static readonly string[] ForbiddenLiterals =
    [
        "VSoftSol Syslog Manager",
        "Vision Software Solutions",
        "vsoftsol.com",
        "support@vsoftsol.com",
        "#0F4C81",
        "#2E9E6B",
        "© 2026 Vision Software Solutions",
    ];

    private static readonly string[] ScannedExtensions =
        [".cs", ".razor", ".cshtml", ".css", ".json", ".csproj", ".props", ".targets"];

    [Fact]
    public void ProductSource_ContainsNoHardcodedBrandStrings_ExceptGeneratedBrandingInfo()
    {
        string srcRoot = RepoPaths.Src;
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
        {
            if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsGeneratedBrandingInfo(file) || IsBuildOutput(file))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string literal in ForbiddenLiterals)
            {
                if (text.Contains(literal, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(srcRoot, file)} :: \"{literal}\"");
                }
            }
        }

        offenders.Should().BeEmpty(
            "brand values must be read from BrandingInfo / CSS custom properties, never hardcoded");
    }

    [Fact]
    public void GeneratedBrandingInfo_Exists_AfterBuild()
    {
        File.Exists(RepoPaths.GeneratedBrandingInfo)
            .Should().BeTrue("the branding pipeline generates it before Core compiles");
    }

    private static bool IsGeneratedBrandingInfo(string file) =>
        Path.GetFileName(file).Equals("BrandingInfo.g.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string file)
    {
        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            // Generated, git-ignored branding output (BRANDING.md): brand.css and the
            // derived assets legitimately embed brand values.
            || normalized.Contains("/wwwroot/branding/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Generated/", StringComparison.OrdinalIgnoreCase);
    }
}
