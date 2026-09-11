using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VSoftSol.Syslog.Core.Retention.Compression;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>
/// The archive content format (PHASE_10 build item 4) — one JSON header line, then one JSON
/// line per event (NDJSON), the whole thing compressed as a single <see cref="CompressorFactory"/>
/// blob. No zip, no internal file entries — nothing about this format has a "path" a
/// malicious entry could redirect, which is a large part of how it avoids zip-slip
/// (SECURITY_STANDARDS.md "Malicious archive import ... zip-slip").
/// </summary>
internal static class ArchiveFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed record ArchiveHeader
    {
        public required string Product { get; init; }
        public required string Version { get; init; }
        public long? StreamId { get; init; }
        public required string StreamName { get; init; }
        public required DateTimeOffset PeriodStartUtc { get; init; }
        public required DateTimeOffset PeriodEndUtc { get; init; }
        public required int EventCount { get; init; }
        public required DateTimeOffset ExportedUtc { get; init; }
    }

    public sealed record ArchiveFieldRow(string Name, string Value);

    public sealed record ArchiveEventRow
    {
        public long EventId { get; init; }
        public required string ReceivedUtc { get; init; }
        public string? EventUtc { get; init; }
        public required string SourceIp { get; init; }
        public string? Hostname { get; init; }
        public string? AppName { get; init; }
        public string? ProcId { get; init; }
        public string? MsgId { get; init; }
        public int Facility { get; init; }
        public int Severity { get; init; }
        public required string Protocol { get; init; }
        public long ListenerId { get; init; }
        public required string Message { get; init; }

        /// <summary>Base64 of the original raw bytes — always the true original, never the
        /// Warm-compressed form (a row is decompressed before archiving; the whole archive
        /// file is then compressed once, at the archive's own level).</summary>
        public required string RawMessageBase64 { get; init; }

        public required string ParseStatus { get; init; }
        public int OccurrenceCount { get; init; } = 1;
        public string? StructuredDataJson { get; init; }
        public long? DeviceId { get; init; }
        public string? Vendor { get; init; }
        public List<ArchiveFieldRow> Fields { get; init; } = [];
        public List<long> StreamIds { get; init; } = [];
    }

    public sealed record ParsedArchive(ArchiveHeader Header, IReadOnlyList<ArchiveEventRow> Rows);

    public static byte[] Build(ArchiveHeader header, IReadOnlyList<ArchiveEventRow> rows, CompressorFactory compressor, int level)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(compressor);

        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(header, JsonOptions)).Append('\n');
        foreach (ArchiveEventRow row in rows)
        {
            text.Append(JsonSerializer.Serialize(row, JsonOptions)).Append('\n');
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(text.ToString());
        return compressor.Compress(plaintext, level);
    }

    /// <summary>Refuses (throws <see cref="InvalidDataException"/>) before allocating more
    /// than <paramref name="maxDecompressedBytes"/> — the decompression-bomb defence applies
    /// to every archive read, restore included.</summary>
    public static ParsedArchive Parse(byte[] compressedFileBytes, CompressorFactory compressor, long maxDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(compressedFileBytes);
        ArgumentNullException.ThrowIfNull(compressor);

        byte[] plaintext = compressor.Decompress(compressedFileBytes, maxDecompressedBytes);
        using var reader = new StringReader(Encoding.UTF8.GetString(plaintext));

        string? headerLine = reader.ReadLine();
        if (string.IsNullOrEmpty(headerLine))
        {
            throw new InvalidDataException("Archive is empty or missing its header line.");
        }

        ArchiveHeader header;
        try
        {
            header = JsonSerializer.Deserialize<ArchiveHeader>(headerLine, JsonOptions)
                ?? throw new InvalidDataException("Archive header parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Archive header is not valid JSON.", ex);
        }

        var rows = new List<ArchiveEventRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                rows.Add(JsonSerializer.Deserialize<ArchiveEventRow>(line, JsonOptions)
                    ?? throw new InvalidDataException("An archive event row parsed to null."));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("An archive event row is not valid JSON — refusing the whole restore.", ex);
            }
        }

        return new ParsedArchive(header, rows);
    }
}
