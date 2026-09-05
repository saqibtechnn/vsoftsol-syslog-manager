using System.Globalization;
using System.Text;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Data.Repositories;

/// <summary>Value ⇄ storage-string conversions for the event store. All times are UTC.</summary>
internal static class StorageFormat
{
    private static readonly UTF8Encoding LossyUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();

    public static DateTimeOffset? ParseTimestampOrNull(string? value) =>
        value is null ? null : ParseTimestamp(value);

    /// <summary>Lossy UTF-8 projection of the raw bytes, for FTS indexing only. The BLOB keeps the originals.</summary>
    public static string RawText(ReadOnlyMemory<byte> raw) => SanitizeText(LossyUtf8.GetString(raw.Span));

    /// <summary>
    /// Replaces NUL with the Unicode replacement character. SQLite TEXT columns are read
    /// back as NUL-terminated C strings, so an embedded NUL would silently truncate the
    /// stored value (and breaks the FTS tokenizer). The verbatim bytes are always kept in
    /// the <c>raw_message</c> BLOB (CLAUDE.md Constraint 4); this only affects the
    /// searchable text projection and the parsed <c>message</c> column.
    /// </summary>
    public static string SanitizeText(string value) =>
        value.Contains('\0', StringComparison.Ordinal) ? value.Replace('\0', '�') : value;

    public static string Protocol(Protocol value) => value switch
    {
        Core.Enums.Protocol.Udp => "udp",
        Core.Enums.Protocol.Tcp => "tcp",
        Core.Enums.Protocol.Tls => "tls",
        Core.Enums.Protocol.Snmp => "snmp",
        Core.Enums.Protocol.WinEventLog => "wineventlog",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown protocol."),
    };

    public static Protocol ParseProtocol(string value) => value switch
    {
        "udp" => Core.Enums.Protocol.Udp,
        "tcp" => Core.Enums.Protocol.Tcp,
        "tls" => Core.Enums.Protocol.Tls,
        "snmp" => Core.Enums.Protocol.Snmp,
        "wineventlog" => Core.Enums.Protocol.WinEventLog,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown protocol token."),
    };

    public static string ParseStatus(ParseStatus value) => value switch
    {
        Core.Enums.ParseStatus.Raw => "raw",
        Core.Enums.ParseStatus.Rfc3164 => "rfc3164",
        Core.Enums.ParseStatus.Rfc5424 => "rfc5424",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown parse status."),
    };

    public static ParseStatus ParseParseStatus(string value) => value switch
    {
        "raw" => Core.Enums.ParseStatus.Raw,
        "rfc3164" => Core.Enums.ParseStatus.Rfc3164,
        "rfc5424" => Core.Enums.ParseStatus.Rfc5424,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown parse status token."),
    };
}
