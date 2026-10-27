using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Installer;

/// <summary>
/// Regression guard for the v1.1.1 installer bug (docs/evidence/v1.1-installer-dotnet-check-fix):
/// the .NET Hosting Bundle check read the 32-bit registry view, so it failed on every first
/// install. An MSI launch condition has no runtime test harness here, so these read the WiX
/// source and pin the key, the bitness and the condition.
/// </summary>
public sealed class InstallerPrerequisiteCheckTests
{
    private const string NativeSharedHostKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost";

    private static XElement Load(params string[] relativePath) =>
        XDocument.Load(Path.Combine([RepoPaths.Root, .. relativePath])).Root!;

    private static XElement Single(XElement root, string localName, string id) =>
        root.Descendants().Single(e => e.Name.LocalName == localName && (string?)e.Attribute("Id") == id);

    [Fact]
    public void MsiRegistrySearch_ReadsTheNativeSharedHostKey_NotTheWow6432NodeView()
    {
        XElement search = Single(Load("installer", "Product.wxs"), "RegistrySearch", "DotNetSharedHostSearch");

        ((string?)search.Attribute("Key")).Should().Be(NativeSharedHostKey);
        ((string?)search.Attribute("Key")).Should().NotContain("WOW6432Node");
    }

    [Fact]
    public void MsiLaunchCondition_PassesOnUpgradeOrWhenTheSharedHostWasFound()
    {
        XElement launch = Load("installer", "Product.wxs").Descendants()
            .Single(e => e.Name.LocalName == "Launch");

        ((string?)launch.Attribute("Condition")).Should().Be("Installed OR DOTNETSHAREDHOST");
    }

    [Fact]
    public void BundleRegistrySearch_ReadsTheSameNativeKey_InThe64BitView()
    {
        XElement search = Single(
            Load("installer", "Bundle", "Bundle.wxs"), "RegistrySearch", "DotNetSharedHostSearch");

        ((string?)search.Attribute("Key")).Should().Be(NativeSharedHostKey);
        ((string?)search.Attribute("Bitness")).Should().Be("always64");
    }
}
