namespace VSoftSol.Syslog.Core.Retention.Compression;

/// <summary>
/// Tags every compressed blob with a one-byte <see cref="CompressionFormat"/> header so
/// decompression always dispatches to the right backend — including historical data
/// written back when a different backend was primary (PHASE_10 build item 3).
/// </summary>
public sealed class CompressorFactory
{
    private readonly IMessageCompressor _primary;
    private readonly Dictionary<CompressionFormat, IMessageCompressor> _all;

    public CompressorFactory()
        : this(ProbePrimary())
    {
    }

    /// <summary>Test seam — inject a specific primary without touching the Zstd probe.</summary>
    public CompressorFactory(IMessageCompressor primary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _all = new Dictionary<CompressionFormat, IMessageCompressor>
        {
            [CompressionFormat.Zstd] = primary.Format == CompressionFormat.Zstd ? primary : new ZstdMessageCompressor(),
            [CompressionFormat.Gzip] = primary.Format == CompressionFormat.Gzip ? primary : new GzipMessageCompressor(),
        };
    }

    public CompressionFormat PrimaryFormat => _primary.Format;

    public byte[] Compress(ReadOnlySpan<byte> data, int level)
    {
        byte[] body = _primary.Compress(data, level);
        var tagged = new byte[body.Length + 1];
        tagged[0] = (byte)_primary.Format;
        body.CopyTo(tagged.AsSpan(1));
        return tagged;
    }

    public byte[] Decompress(ReadOnlySpan<byte> tagged, long maxOutputBytes)
    {
        if (tagged.Length == 0)
        {
            throw new InvalidDataException("Compressed blob is empty — missing format tag.");
        }

        var format = (CompressionFormat)tagged[0];
        if (!_all.TryGetValue(format, out IMessageCompressor? compressor))
        {
            throw new InvalidDataException($"Unknown compression format tag {tagged[0]}.");
        }

        return compressor.Decompress(tagged[1..], maxOutputBytes);
    }

    private static IMessageCompressor ProbePrimary()
    {
        try
        {
            var zstd = new ZstdMessageCompressor();
            byte[] probe = "vsoftsol-zstd-probe"u8.ToArray();
            byte[] compressed = zstd.Compress(probe, 1);
            byte[] roundTrip = zstd.Decompress(compressed, probe.Length + 64);
            return roundTrip.AsSpan().SequenceEqual(probe) ? zstd : new GzipMessageCompressor();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or PlatformNotSupportedException or TypeInitializationException)
        {
            return new GzipMessageCompressor();
        }
    }
}
