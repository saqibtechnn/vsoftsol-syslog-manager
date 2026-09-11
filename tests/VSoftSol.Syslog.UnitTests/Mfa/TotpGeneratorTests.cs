using FluentAssertions;
using VSoftSol.Syslog.Core.Security.Mfa;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Mfa;

[Trait("Category", "Mfa")]
public sealed class TotpGeneratorTests
{
    // RFC 6238 Appendix B test vector: 20-byte ASCII secret "12345678901234567890",
    // SHA-1, 8 digits, T=59s -> code "94287082".
    private static readonly byte[] RfcSecret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");

    [Fact]
    public void GenerateCode_RfcTestVector_MatchesTheKnownAnswer()
    {
        string code = TotpGenerator.GenerateCode(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(59), digits: 8);
        code.Should().Be("94287082");
    }

    [Fact]
    public void ValidateCode_TheCurrentCode_IsAccepted()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string code = TotpGenerator.GenerateCode(RfcSecret, now);

        TotpGenerator.ValidateCode(RfcSecret, code, now).Should().BeTrue();
    }

    [Fact]
    public void ValidateCode_OneStepOfClockDrift_IsStillAccepted()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string code = TotpGenerator.GenerateCode(RfcSecret, now);

        TotpGenerator.ValidateCode(RfcSecret, code, now.AddSeconds(30), window: 1).Should().BeTrue();
    }

    [Fact]
    public void ValidateCode_FarOutsideTheWindow_IsRejected()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string code = TotpGenerator.GenerateCode(RfcSecret, now);

        TotpGenerator.ValidateCode(RfcSecret, code, now.AddMinutes(10), window: 1).Should().BeFalse();
    }

    [Fact]
    public void ValidateCode_WrongCode_IsRejected()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TotpGenerator.ValidateCode(RfcSecret, "000000", now).Should().BeFalse();
    }
}
