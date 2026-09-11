namespace VSoftSol.Syslog.Core.Retention.Compression;

/// <summary>
/// A one-byte tag prefixed onto every compressed blob (warm rows and archive files) so
/// decompression is self-describing regardless of which compressor was primary when the
/// data was written (PHASE_10 build item 3: "Zstd ... with gzip fallback if Zstd is
/// unavailable"). <see cref="None"/> is a reserved sentinel, never produced.
/// </summary>
public enum CompressionFormat : byte
{
    None = 0,
    Zstd = 1,
    Gzip = 2,
}
