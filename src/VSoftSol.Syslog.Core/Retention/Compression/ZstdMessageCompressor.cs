using ZstdSharp;

namespace VSoftSol.Syslog.Core.Retention.Compression;

/// <summary>The primary compressor (PHASE_10 build item 3). ZstdSharp is a pure-managed
/// port of zstd (MIT, no native binary, no P/Invoke) — it carries no platform risk, but
/// <see cref="CompressorFactory"/> still probes it at startup and falls back to
/// <see cref="GzipMessageCompressor"/> if construction or a round-trip ever fails.</summary>
public sealed class ZstdMessageCompressor : IMessageCompressor
{
    public CompressionFormat Format => CompressionFormat.Zstd;

    public byte[] Compress(ReadOnlySpan<byte> data, int level)
    {
        using var compressor = new Compressor(Math.Clamp(level, 1, 19));
        return compressor.Wrap(data).ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, long maxOutputBytes)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zstd = new DecompressionStream(input);
        return BoundedCopy.ReadAll(zstd, maxOutputBytes);
    }
}
