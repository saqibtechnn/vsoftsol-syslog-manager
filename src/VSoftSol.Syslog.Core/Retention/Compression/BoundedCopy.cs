namespace VSoftSol.Syslog.Core.Retention.Compression;

/// <summary>
/// Reads a decompression <see cref="Stream"/> to completion in fixed-size chunks, aborting
/// the moment the running total exceeds a cap — format-agnostic decompression-bomb defence
/// that does not depend on trusting any compressor's self-reported "decompressed size"
/// metadata (which a crafted file can misstate).
/// </summary>
internal static class BoundedCopy
{
    private const int ChunkSize = 81_920;

    public static byte[] ReadAll(Stream source, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var output = new MemoryStream();
        byte[] buffer = new byte[ChunkSize];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed content exceeds the {maxBytes:N0}-byte cap — refusing (possible decompression bomb).");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
