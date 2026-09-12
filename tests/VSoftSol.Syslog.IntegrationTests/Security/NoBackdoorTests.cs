using System.Text;
using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Security;

/// <summary>
/// PHASE_12: an automated, best-effort assertion against the actual shipping binaries (not
/// source, which the rest of the suite already covers) that no hardcoded bypass credential,
/// debug backdoor, or "skip auth" literal made it into the packaged build. This is one
/// layer among several, not a substitute for the others: it is scoped to look for the
/// clumsy, literal-string form of backdoor — <see cref="VSoftSol.Syslog.IntegrationTests.Auth.AuthorizationMatrixTests"/>
/// independently proves the structural property that every routable page either allows
/// anonymous access as a reviewed, explicit exception, or requires a named authorization
/// policy, which is the property that actually rules out a hidden unauthenticated route.
/// </summary>
[Trait("Category", "Security")]
public sealed class NoBackdoorTests
{
    private static readonly string[] BannedLiterals =
    [
        "backdoor",
        "skipauth",
        "skip_auth",
        "bypassauth",
        "bypass_auth",
        "godmode",
        "god_mode",
        "debuglogin",
        "debug_login",
        "masterpassword",
        "master_password",
        "letmein",
    ];

    public static IEnumerable<object[]> PublishedAssemblies()
    {
        string publishDir = Path.Combine(RepoRoot(), "publish", "Web");
        if (!Directory.Exists(publishDir))
        {
            yield break; // publish/Web is a build artifact, not always present when this suite runs
        }

        foreach (string dll in Directory.EnumerateFiles(publishDir, "VSoftSol.Syslog.*.dll", SearchOption.TopDirectoryOnly))
        {
            yield return [dll];
        }
    }

    [Theory]
    [MemberData(nameof(PublishedAssemblies))]
    public void PublishedAssembly_ContainsNoBannedBackdoorLiteral(string dllPath)
    {
        byte[] bytes = File.ReadAllBytes(dllPath);

        // .NET string literals are UTF-16LE in the metadata #US heap; also scan as plain
        // UTF-8 in case anything routes through a resource, config blob, or byte array
        // literal instead of a normal C# string constant.
        string utf16 = Encoding.Unicode.GetString(bytes);
        string utf8 = Encoding.UTF8.GetString(bytes);

        List<string> offenders = [];
        foreach (string banned in BannedLiterals)
        {
            if (utf16.Contains(banned, StringComparison.OrdinalIgnoreCase) ||
                utf8.Contains(banned, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(banned);
            }
        }

        offenders.Should().BeEmpty($"{Path.GetFileName(dllPath)} must not contain a hardcoded backdoor/bypass literal");
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        return dir ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
