using FluentAssertions;
using VSoftSol.Syslog.Core.Updates;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Updates;

[Trait("Category", "Updates")]
public sealed class ProductVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2.3-rc1", 1, 2, 3)]
    [InlineData(" v1.2.3 ", 1, 2, 3)]
    // The SDK stamps every build with the source commit as semver build metadata ("1.1.3+<sha>"),
    // and that is what the running product reads back as its own version.
    [InlineData("1.1.3+7da4e5593b52f78527fd392cef8d4097eb4a4db5", 1, 1, 3)]
    [InlineData("1.1.3-rc1+7da4e55", 1, 1, 3)]
    public void TryParse_WellFormedVersions_Parse(string input, int major, int minor, int patch)
    {
        ProductVersion.TryParse(input, out (int Major, int Minor, int Patch) parsed).Should().BeTrue();
        parsed.Should().Be((major, minor, patch));
    }

    [Fact]
    public void TheVersionTheBuiltProductReportsAboutItself_IsParseableByTheUpdateChecker()
    {
        // Exactly what UpdateChecker is given as its current version (SyslogPlatformExtensions.CurrentAssemblyVersion).
        string? reported = System.Diagnostics.FileVersionInfo
            .GetVersionInfo(typeof(ProductVersion).Assembly.Location).ProductVersion;

        ProductVersion.TryParse(reported, out (int Major, int Minor, int Patch) parsed)
            .Should().BeTrue($"the product reports its version as \"{reported}\"; an unparseable value makes every update look like it is not newer");
        parsed.Major.Should().BeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("-1.2.3")]
    [InlineData("not a version at all")]
    public void TryParse_MalformedInput_NeverThrows_ReturnsFalse(string? input)
    {
        ProductVersion.TryParse(input, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.1.4", "1.1.3+7da4e5593b52f78527fd392cef8d4097eb4a4db5", true)]
    [InlineData("v1.1.4", "1.1.3+680fa9f097649c37ce13cf31ba2b3fa2dfea91ed", true)]
    [InlineData("1.1.3", "1.1.3+7da4e5593b52f78527fd392cef8d4097eb4a4db5", false)]
    [InlineData("1.1.2", "1.1.3+7da4e5593b52f78527fd392cef8d4097eb4a4db5", false)]
    public void IsNewer_WhenTheRunningVersionCarriesBuildMetadata_StillComparesTheNumbers(string candidate, string current, bool expected)
    {
        ProductVersion.IsNewer(candidate, current).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("v1.2.0", "1.1.0", true)]
    public void IsNewer_ComparesDottedTriples(string candidate, string current, bool expected)
    {
        ProductVersion.IsNewer(candidate, current).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("")]
    public void IsNewer_UnparseableCandidate_NeverThrows_IsNotNewer(string? candidate)
    {
        ProductVersion.IsNewer(candidate, "1.0.0").Should().BeFalse();
    }
}
