namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>Finds the built VSoftSol.Syslog.CrashProbe.dll next to the test output.</summary>
internal static class CrashProbeLocator
{
    public static string ExecutablePath()
    {
        // tests/VSoftSol.Syslog.IntegrationTests/bin/<config>/net8.0  ->
        // tests/VSoftSol.Syslog.CrashProbe/bin/<config>/net8.0/VSoftSol.Syslog.CrashProbe.dll
        var testBin = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = testBin.Parent!.Name;      // Debug | Release
        string tfm = testBin.Name;                        // net8.0

        DirectoryInfo? testsRoot = testBin.Parent!.Parent!.Parent!.Parent;   // tests/
        string candidate = Path.Combine(
            testsRoot!.FullName,
            "VSoftSol.Syslog.CrashProbe", "bin", configuration, tfm, "VSoftSol.Syslog.CrashProbe.dll");

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"Crash probe build output not found. Build the solution first. Looked at: {candidate}");
        }

        return candidate;
    }
}
