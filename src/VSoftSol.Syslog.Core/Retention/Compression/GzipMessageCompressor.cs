using System.IO.Compression;

namespace VSoftSol.Syslog.Core.Retention.Compression;

/// <summary>The dependency-free fallback compressor (PHASE_10 build item 3) — used when
/// <see cref="ZstdMessageCompressor"/> cannot be constructed on this platform, and always
/// registered for decompression so historical Gzip-tagged data stays readable.</summary>
public sealed class GzipMessageCompressor : IMessageCompressor
{
    public CompressionFormat Format => CompressionFormat.Gzip;

    public byte[] Compress(ReadOnlySpan<byte> data, int level)
    {
        CompressionLevel gzipLevel = level switch
        {
            <= 3 => CompressionLevel.Fastest,
            >= 8 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, gzipLevel, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, long maxOutputBytes)
    {
        using var input = new MemoryStream(data.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return BoundedCopy.ReadAll(gzip, maxOutputBytes);
    }
}
