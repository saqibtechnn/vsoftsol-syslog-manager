using System.Text;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Retention.Compression;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>
/// The single decompression chokepoint for a Warm-tier <c>events</c> row (PHASE_10 build
/// item 3). <see cref="Repositories.EventRowMapper"/> (search, exports, reports, alert
/// window reads) and <see cref="Repositories.SqliteLogRepository"/>'s own row reader
/// (context view, get-by-id) both call this — one place decides how a compressed row
/// becomes plain <c>message</c>/<c>raw_message</c> again, so search and every other read
/// path see byte-identical content whether the row is Hot or Warm (PHASE_10 "Warm-tier
/// search test"). Decompression is capped defensively even though the source is our own
/// database, not network input — SECURITY_STANDARDS.md's hostile-input discipline extends
/// to a file-system-level tamper of the database too.
/// </summary>
internal static class WarmTierCodec
{
    private const long MaxDecompressedFieldBytes = 16 * 1024 * 1024; // far beyond any real syslog message
    private static readonly CompressorFactory Decompressor = new(new GzipMessageCompressor());

    public static (string Message, byte[] Raw) Decode(bool warm, SqliteDataReader reader, int messageIndex, int rawIndex)
    {
        if (!warm)
        {
            return (reader.GetString(messageIndex), (byte[])reader[rawIndex]);
        }

        byte[] messageBytes = Decompressor.Decompress((byte[])reader[messageIndex], MaxDecompressedFieldBytes);
        byte[] raw = Decompressor.Decompress((byte[])reader[rawIndex], MaxDecompressedFieldBytes);
        return (Encoding.UTF8.GetString(messageBytes), raw);
    }
}
