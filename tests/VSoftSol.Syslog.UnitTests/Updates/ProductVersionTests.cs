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
    public void TryParse_WellFormedVersions_Parse(string input, int major, int minor, int patch)
    {
        ProductVersion.TryParse(input, out (int Major, int Minor, int Patch) parsed).Should().BeTrue();
        parsed.Should().Be((major, minor, patch));
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
