using VSoftSol.Syslog.BrandingGen;

// Usage: VSoftSol.Syslog.BrandingGen <repoRoot> [warningsFile]
// Warnings are printed to stdout and, if a warningsFile is given, written there one per
// line (the file is always rewritten so stale warnings clear). A missing logo is a
// warning, not a failure: exit 0. Non-zero exit means a real I/O failure.
if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("ERROR: repoRoot argument is required.");
    return 2;
}

string repoRoot = Path.GetFullPath(args[0]);
string? warningsFile = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : null;

try
{
    BrandingResult result = BrandingGenerator.Generate(repoRoot);

    foreach (string warning in result.Warnings)
    {
        Console.Out.WriteLine("WARNING: " + warning);
    }

    if (warningsFile is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(warningsFile)!);
        File.WriteAllLines(warningsFile, result.Warnings);
    }

    Console.Out.WriteLine("branding: generated " + result.GeneratedInfoPath);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: branding generation failed: " + ex);
    return 1;
}
