using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class PayloadDecoderTests
{
    [Fact]
    public void Decode_PlainUtf8_ReturnsTextAndUtf8Charset()
    {
        PayloadDecoder.Decoded d = PayloadDecoder.Decode("héllo wörld"u8.ToArray(), 1000);

        d.Text.Should().Be("héllo wörld");
        d.Charset.Should().Be("utf-8");
        d.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Decode_Utf8Bom_IsStripped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "after bom"u8];
        PayloadDecoder.Decoded d = PayloadDecoder.Decode(bytes, 1000);

        d.Text.Should().Be("after bom");
        d.Charset.Should().Be("utf-8");
    }

    [Fact]
    public void Decode_InvalidUtf8_FallsBackToLatin1_WithoutThrowing()
    {
        // 0xE9 is 'é' in latin-1 but an invalid lone continuation-less lead byte in UTF-8.
        byte[] bytes = [(byte)'c', (byte)'a', (byte)'f', 0xE9];
        PayloadDecoder.Decoded d = PayloadDecoder.Decode(bytes, 1000);

        d.Text.Should().Be("café");
        d.Charset.Should().Be("latin-1");
    }

    [Fact]
    public void Decode_Utf16LeBom_IsDecoded()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("wide")];
        PayloadDecoder.Decoded d = PayloadDecoder.Decode(bytes, 1000);

        d.Text.Should().Be("wide");
        d.Charset.Should().Be("utf-16le");
    }

    [Fact]
    public void Decode_OverTheCharCap_IsTruncated_AndFlagged()
    {
        PayloadDecoder.Decoded d = PayloadDecoder.Decode(Encoding.UTF8.GetBytes(new string('x', 500)), 100);

        d.Text.Should().HaveLength(100);
        d.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Decode_EmptyPayload_ReturnsEmptyString()
    {
        PayloadDecoder.Decode([], 100).Text.Should().BeEmpty();
    }
}
