namespace VSoftSol.Syslog.Core.Retention.Compression;

/// <summary>One compression backend. Pure in-memory transform — no file or network I/O.</summary>
public interface IMessageCompressor
{
    CompressionFormat Format { get; }

    byte[] Compress(ReadOnlySpan<byte> data, int level);

    /// <summary>
    /// Decompresses <paramref name="data"/>, refusing (throwing <see cref="InvalidDataException"/>)
    /// if the decompressed content would exceed <paramref name="maxOutputBytes"/> — the
    /// decompression-bomb defence (SECURITY_STANDARDS.md "Malicious archive import").
    /// Streamed, so the refusal happens before the full bomb is ever materialised in memory.
    /// </summary>
    byte[] Decompress(ReadOnlySpan<byte> data, long maxOutputBytes);
}
