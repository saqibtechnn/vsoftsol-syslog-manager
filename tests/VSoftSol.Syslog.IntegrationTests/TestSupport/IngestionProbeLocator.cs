namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>Finds the built VSoftSol.Syslog.IngestionProbe.dll next to the test output.</summary>
internal static class IngestionProbeLocator
{
    public static string DllPath()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = testBin.Parent!.Name;
        string tfm = testBin.Name;
        DirectoryInfo testsRoot = testBin.Parent!.Parent!.Parent!.Parent!;

        string candidate = Path.Combine(
            testsRoot.FullName, "VSoftSol.Syslog.IngestionProbe", "bin", configuration, tfm,
            "VSoftSol.Syslog.IngestionProbe.dll");

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"IngestionProbe build output not found. Looked at: {candidate}");
        }

        return candidate;
    }
}
