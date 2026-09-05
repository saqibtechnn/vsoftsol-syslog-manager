namespace VSoftSol.Syslog.UnitTests;

/// <summary>Locates repository directories from the test output folder.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    public static string Branding => Path.Combine(Root, "branding");

    public static string GeneratedBrandingInfo => Path.Combine(
        Root, "src", "VSoftSol.Syslog.Core", "Generated", "BrandingInfo.g.cs");

    private static string FindRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VSoftSol.Syslog.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (VSoftSol.Syslog.sln).");
    }
}
