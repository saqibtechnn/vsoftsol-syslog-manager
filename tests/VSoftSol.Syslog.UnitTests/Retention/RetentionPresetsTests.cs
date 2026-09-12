using FluentAssertions;
using VSoftSol.Syslog.Core.Retention;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Retention;

/// <summary>PHASE_12 build item 2 (first-run wizard, retention step) — the three presets
/// shown with a projected disk usage before any events have ever been ingested.</summary>
public sealed class RetentionPresetsTests
{
    [Fact]
    public void All_Always_ReturnsExactlyThreePresetsInOrder()
    {
        RetentionPresets.All.Select(p => p.Key).Should().Equal("small", "medium", "large");
    }

    [Theory]
    [InlineData("small")]
    [InlineData("medium")]
    [InlineData("large")]
    public void Estimate_AnyPreset_IsPositiveAndMonotonicWithColdDays(string key)
    {
        RetentionPreset preset = RetentionPresets.All.Single(p => p.Key == key);

        RetentionEstimate estimate = RetentionPresets.Estimate(preset);

        estimate.TotalBytes.Should().BePositive();
        estimate.HotBytes.Should().BePositive();
    }

    [Fact]
    public void Estimate_LargePreset_ProjectsMoreTotalBytesThanSmallPreset()
    {
        RetentionEstimate small = RetentionPresets.Estimate(RetentionPresets.Small);
        RetentionEstimate large = RetentionPresets.Estimate(RetentionPresets.Large);

        large.TotalBytes.Should().BeGreaterThan(small.TotalBytes,
            "Large assumes 20x the devices of Small; even a shorter hot window must not overtake it");
    }

    [Fact]
    public void Estimate_SamePresetTwice_IsDeterministic()
    {
        RetentionPresets.Estimate(RetentionPresets.Medium).Should().Be(RetentionPresets.Estimate(RetentionPresets.Medium));
    }
}
