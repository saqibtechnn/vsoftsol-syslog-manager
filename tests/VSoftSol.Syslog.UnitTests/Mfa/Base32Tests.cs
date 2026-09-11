using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Security.Mfa;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Mfa;

[Trait("Category", "Mfa")]
public sealed class Base32Tests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsArbitraryBytes()
    {
        byte[] original = Encoding.UTF8.GetBytes("a 20-byte totp secret");
        string encoded = Base32.Encode(original);

        Base32.Decode(encoded).Should().Equal(original);
    }

    [Fact]
    public void Decode_ToleratesLowerCaseAndMissingPadding()
    {
        byte[] original = [1, 2, 3, 4, 5];
        string encoded = Base32.Encode(original);

        Base32.Decode(encoded.ToLowerInvariant()).Should().Equal(original);
    }

    [Fact]
    public void Decode_RejectsAnInvalidCharacter()
    {
        Action act = () => Base32.Decode("1NVALID!");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Encode_KnownVector_MatchesRfc4648()
    {
        // RFC 4648 §10 test vector: "foobar" -> "MZXW6YTBOI======" (padding stripped here).
        Base32.Encode(Encoding.ASCII.GetBytes("foobar")).Should().Be("MZXW6YTBOI");
    }
}
