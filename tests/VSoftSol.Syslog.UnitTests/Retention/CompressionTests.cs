using FluentAssertions;
using VSoftSol.Syslog.Core.Retention.Compression;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Retention;

/// <summary>PHASE_10 — the Zstd/Gzip round trip and the tag-byte dispatch that makes
/// decompression self-describing, plus the decompression-bomb defence
/// (SECURITY_STANDARDS.md "Malicious archive import ... a decompression bomb").</summary>
public sealed class CompressionTests
{
    private static readonly byte[] Sample = "The quick brown fox jumps over the lazy dog. "u8.ToArray();

    [Theory]
    [MemberData(nameof(Compressors))]
    public void Compress_ThenDecompress_RoundTripsExactly(IMessageCompressor compressor)
    {
        byte[] compressed = compressor.Compress(Sample, level: 3);
        byte[] roundTrip = compressor.Decompress(compressed, maxOutputBytes: Sample.Length * 2);

        roundTrip.Should().Equal(Sample);
    }

    [Theory]
    [MemberData(nameof(Compressors))]
    public void Compress_OfRepetitiveData_ActuallyShrinks(IMessageCompressor compressor)
    {
        byte[] repetitive = new byte[10_000];
        Array.Fill(repetitive, (byte)'a');

        byte[] compressed = compressor.Compress(repetitive, level: 3);

        compressed.Length.Should().BeLessThan(repetitive.Length);
    }

    [Theory]
    [MemberData(nameof(Compressors))]
    public void Decompress_BeyondTheCap_ThrowsInsteadOfMaterialisingTheBomb(IMessageCompressor compressor)
    {
        byte[] big = new byte[200_000];
        Array.Fill(big, (byte)'z'); // highly compressible -> tiny compressed size, huge ratio
        byte[] compressed = compressor.Compress(big, level: 9);

        Action act = () => compressor.Decompress(compressed, maxOutputBytes: 1_000);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void CompressorFactory_TagsWithItsPrimaryFormat_AndDecompressesItsOwnOutput()
    {
        var factory = new CompressorFactory(new ZstdMessageCompressor());

        byte[] tagged = factory.Compress(Sample, level: 3);
        tagged[0].Should().Be((byte)VSoftSol.Syslog.Core.Retention.Compression.CompressionFormat.Zstd);

        factory.Decompress(tagged, Sample.Length * 2).Should().Equal(Sample);
    }

    [Fact]
    public void CompressorFactory_DecompressesGzipTaggedData_EvenWhenZstdIsPrimary()
    {
        var factory = new CompressorFactory(new ZstdMessageCompressor());
        var gzip = new GzipMessageCompressor();
        byte[] gzipBody = gzip.Compress(Sample, 3);
        byte[] tagged = [(byte)CompressionFormat.Gzip, .. gzipBody];

        factory.Decompress(tagged, Sample.Length * 2).Should().Equal(Sample);
    }

    [Fact]
    public void CompressorFactory_WithGzipPrimary_TagsAsGzip()
    {
        var factory = new CompressorFactory(new GzipMessageCompressor());

        byte[] tagged = factory.Compress(Sample, level: 3);

        tagged[0].Should().Be((byte)CompressionFormat.Gzip);
        factory.PrimaryFormat.Should().Be(CompressionFormat.Gzip);
    }

    [Fact]
    public void CompressorFactory_Decompress_OfAnEmptyBlob_Throws()
    {
        var factory = new CompressorFactory(new GzipMessageCompressor());
        Action act = () => factory.Decompress([], 1_000);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void CompressorFactory_Decompress_OfAnUnknownFormatTag_Throws()
    {
        var factory = new CompressorFactory(new GzipMessageCompressor());
        Action act = () => factory.Decompress([255, 1, 2, 3], 1_000);
        act.Should().Throw<InvalidDataException>();
    }

    public static TheoryData<IMessageCompressor> Compressors() =>
        new() { new ZstdMessageCompressor(), new GzipMessageCompressor() };
}
