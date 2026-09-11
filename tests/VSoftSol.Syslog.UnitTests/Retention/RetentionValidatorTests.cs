using FluentAssertions;
using VSoftSol.Syslog.Core.Retention;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Retention;

/// <summary>PHASE_10 — retention policy/settings validation.</summary>
public sealed class RetentionValidatorTests
{
    private static RetentionPolicy ValidPolicy() => new() { HotDays = 30, WarmDays = 90, ColdDays = 365, CompressionLevel = 3 };

    [Fact]
    public void Validate_Policy_WithDefaults_Succeeds()
    {
        RetentionValidator.Validate(ValidPolicy()).Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, 90, 365)]
    [InlineData(30, -5, 365)]
    [InlineData(30, 90, -1)]
    public void Validate_Policy_WithNegativeDays_Fails(int hot, int warm, int cold)
    {
        RetentionValidationResult result = RetentionValidator.Validate(ValidPolicy() with { HotDays = hot, WarmDays = warm, ColdDays = cold });

        result.Ok.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_Policy_WithDaysBeyondTenYears_Fails()
    {
        RetentionValidator.Validate(ValidPolicy() with { HotDays = RetentionValidator.MaxDays + 1 }).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void Validate_Policy_WithCompressionLevelOutOfRange_Fails(int level)
    {
        RetentionValidator.Validate(ValidPolicy() with { CompressionLevel = level }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_Policy_WithArchivePathContainingControlCharacters_Fails()
    {
        RetentionValidator.Validate(ValidPolicy() with { ArchivePath = "C:\\archives\r\n" }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_Policy_WithNullArchivePath_Succeeds()
    {
        RetentionValidator.Validate(ValidPolicy() with { ArchivePath = null }).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_Settings_WithDefaults_Succeeds()
    {
        RetentionValidator.Validate(new RetentionSettings()).Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200_000)]
    public void Validate_Settings_WithBatchSizeOutOfRange_Fails(int batchSize)
    {
        RetentionValidator.Validate(new RetentionSettings { BatchSize = batchSize }).Ok.Should().BeFalse();
    }

    [Fact]
    public void ColdThresholdDays_IsHotPlusWarm()
    {
        var policy = new RetentionPolicy { HotDays = 30, WarmDays = 90 };
        policy.WarmThresholdDays.Should().Be(30);
        policy.ColdThresholdDays.Should().Be(120);
    }
}
