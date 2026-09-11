using FluentAssertions;
using VSoftSol.Syslog.Core.Security.Mfa;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Mfa;

[Trait("Category", "Mfa")]
public sealed class RecoveryCodeGeneratorTests
{
    [Fact]
    public void Generate_ProducesTheRequestedCountOfDistinctCodes()
    {
        IReadOnlyList<string> codes = RecoveryCodeGenerator.Generate(10);

        codes.Should().HaveCount(10);
        codes.Distinct().Should().HaveCount(10, "recovery codes must not repeat");
    }

    [Fact]
    public void HashThenVerify_TheSameCode_Succeeds()
    {
        string code = RecoveryCodeGenerator.Generate(1)[0];
        string hash = RecoveryCodeGenerator.Hash(code);

        RecoveryCodeGenerator.Verify(code, hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_ADifferentCodeAgainstTheSameHash_Fails()
    {
        string code = RecoveryCodeGenerator.Generate(1)[0];
        string hash = RecoveryCodeGenerator.Hash(code);

        RecoveryCodeGenerator.Verify("WRONG-CODE-0000", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_IsCaseInsensitive_SoATranscribedCodeStillWorks()
    {
        string code = RecoveryCodeGenerator.Generate(1)[0];
        string hash = RecoveryCodeGenerator.Hash(code);

        RecoveryCodeGenerator.Verify(code.ToLowerInvariant(), hash).Should().BeTrue();
    }
}
