using FluentAssertions;
using VSoftSol.Syslog.Core.Retention;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Retention;

/// <summary>PHASE_10 — projected disk usage, shown before a retention change is saved
/// (UX_STANDARDS.md: "never let a user configure retention blind").</summary>
public sealed class RetentionEstimatorTests
{
    [Fact]
    public void Estimate_HotBytes_IsEventsPerDayTimesDaysTimesAverageSize_Uncompressed()
    {
        var policy = new RetentionPolicy { HotDays = 10, WarmDays = 0, ColdDays = 0 };

        RetentionEstimate estimate = RetentionEstimator.Estimate(policy, eventsPerDay: 1000, avgHotEventBytes: 200, compressionRatio: 0.4);

        estimate.HotBytes.Should().Be(1000L * 10 * 200);
    }

    [Fact]
    public void Estimate_WarmBytes_AppliesTheCompressionRatio()
    {
        var policy = new RetentionPolicy { HotDays = 0, WarmDays = 10, ColdDays = 0 };

        RetentionEstimate estimate = RetentionEstimator.Estimate(policy, eventsPerDay: 1000, avgHotEventBytes: 200, compressionRatio: 0.5);

        estimate.WarmBytes.Should().Be((long)(1000 * 10 * 200 * 0.5));
    }

    [Fact]
    public void Estimate_DatabaseBytes_IsHotPlusWarm_ExcludingArchive()
    {
        var policy = new RetentionPolicy { HotDays = 30, WarmDays = 90, ColdDays = 365 };

        RetentionEstimate estimate = RetentionEstimator.Estimate(policy, eventsPerDay: 500, avgHotEventBytes: 300, compressionRatio: 0.35);

        estimate.DatabaseBytes.Should().Be(estimate.HotBytes + estimate.WarmBytes);
        estimate.TotalBytes.Should().Be(estimate.DatabaseBytes + estimate.ArchiveBytes);
    }

    [Fact]
    public void Estimate_WithZeroEventsPerDay_IsAllZero()
    {
        RetentionEstimate estimate = RetentionEstimator.Estimate(new RetentionPolicy(), eventsPerDay: 0, avgHotEventBytes: 500, compressionRatio: 0.4);

        estimate.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void Estimate_WithAnOutOfRangeRatio_FallsBackToTheDefault()
    {
        var policy = new RetentionPolicy { HotDays = 0, WarmDays = 10, ColdDays = 0 };

        RetentionEstimate withBadRatio = RetentionEstimator.Estimate(policy, 1000, 200, compressionRatio: 0);
        RetentionEstimate withDefault = RetentionEstimator.Estimate(policy, 1000, 200, RetentionEstimator.DefaultCompressionRatio);

        withBadRatio.WarmBytes.Should().Be(withDefault.WarmBytes);
    }

    [Fact]
    public void Estimate_WithNegativeEventsPerDay_Throws()
    {
        Action act = () => RetentionEstimator.Estimate(new RetentionPolicy(), eventsPerDay: -1, avgHotEventBytes: 1, compressionRatio: 0.4);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
