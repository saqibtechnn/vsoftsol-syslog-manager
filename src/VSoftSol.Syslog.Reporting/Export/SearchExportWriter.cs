using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Reporting.Export;

/// <summary>
/// Streams a search result set to CSV, JSON, or raw syslog text (PHASE_05 item 7). The
/// writer consumes an <see cref="IAsyncEnumerable{T}"/> and flushes as it goes, so the
/// caller's memory stays flat regardless of result-set size. Output encoding is at the
/// point of writing, never on ingest.
/// </summary>
public static class SearchExportWriter
{
    private static readonly UTF8Encoding LossyUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly string[] Columns =
    [
        "event_id", "received_utc", "event_utc", "severity", "severity_code", "facility", "facility_code",
        "host", "source_ip", "app_name", "proc_id", "msg_id", "vendor", "protocol", "parse_status",
        "occurrence_count", "message", "raw_message", "fields",
    ];

    public static string ContentType(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "text/csv; charset=utf-8",
        ExportFormat.Json => "application/json; charset=utf-8",
        ExportFormat.RawText => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    public static string FileExtension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Json => "json",
        ExportFormat.RawText => "log",
        _ => "txt",
    };

    public static async Task<int> WriteAsync(
        ExportFormat format, TextWriter writer, IAsyncEnumerable<SyslogEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(events);

        return format switch
        {
            ExportFormat.Csv => await WriteCsvAsync(writer, events, cancellationToken).ConfigureAwait(false),
            ExportFormat.Json => await WriteJsonAsync(writer, events, cancellationToken).ConfigureAwait(false),
            ExportFormat.RawText => await WriteRawAsync(writer, events, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format."),
        };
    }

    private static async Task<int> WriteCsvAsync(
        TextWriter writer, IAsyncEnumerable<SyslogEvent> events, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(string.Join(',', Columns.Select(CsvCell))).ConfigureAwait(false);

        int count = 0;
        await foreach (SyslogEvent e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            string[] cells =
            [
                e.EventId.ToString(CultureInfo.InvariantCulture),
                Iso(e.ReceivedUtc),
                e.EventUtc is { } eu ? Iso(eu) : string.Empty,
                e.Severity.ToString(),
                ((int)e.Severity).ToString(CultureInfo.InvariantCulture),
                e.Facility.ToString(),
                ((int)e.Facility).ToString(CultureInfo.InvariantCulture),
                e.Hostname ?? string.Empty,
                e.SourceIp,
                e.AppName ?? string.Empty,
                e.ProcId ?? string.Empty,
                e.MsgId ?? string.Empty,
                e.Vendor ?? string.Empty,
                e.Protocol.ToString(),
                e.ParseStatus.ToString(),
                e.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
                e.Message,
                RawText(e.RawMessage),
                string.Join(" | ", e.Fields.Select(f => $"{f.Name}={f.Value}")),
            ];

            await writer.WriteLineAsync(string.Join(',', cells.Select(c => CsvCell(CsvFormulaGuard.Guard(c)))))
                .ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The default (JavaScriptEncoder.Default) escapes <, >, &, ', + — so a JSON export
        // opened in a browser or embedded in a page cannot break out into markup.
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private sealed record ExportRow(
        long EventId, string ReceivedUtc, string? EventUtc, string Severity, int SeverityCode,
        string Facility, string Host, string SourceIp, string? AppName, string? ProcId, string? MsgId,
        string? Vendor, string Protocol, string ParseStatus, int OccurrenceCount, string Message,
        string RawMessage, IReadOnlyList<ExportField> Fields);

    private sealed record ExportField(string Name, string Value);

    private static async Task<int> WriteJsonAsync(
        TextWriter writer, IAsyncEnumerable<SyslogEvent> events, CancellationToken cancellationToken)
    {
        await writer.WriteAsync('[').ConfigureAwait(false);

        int count = 0;
        bool first = true;
        await foreach (SyslogEvent e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!first)
            {
                await writer.WriteAsync(',').ConfigureAwait(false);
            }

            first = false;

            var row = new ExportRow(
                e.EventId, Iso(e.ReceivedUtc), e.EventUtc is { } eu ? Iso(eu) : null,
                e.Severity.ToString(), (int)e.Severity, e.Facility.ToString(), e.Hostname ?? string.Empty,
                e.SourceIp, e.AppName, e.ProcId, e.MsgId, e.Vendor, e.Protocol.ToString(),
                e.ParseStatus.ToString(), e.OccurrenceCount, e.Message, RawText(e.RawMessage),
                [.. e.Fields.Select(f => new ExportField(f.Name, f.Value))]);

            await writer.WriteAsync(JsonSerializer.Serialize(row, JsonOptions)).ConfigureAwait(false);
            count++;
        }

        await writer.WriteAsync(']').ConfigureAwait(false);
        return count;
    }

    private static async Task<int> WriteRawAsync(
        TextWriter writer, IAsyncEnumerable<SyslogEvent> events, CancellationToken cancellationToken)
    {
        int count = 0;
        await foreach (SyslogEvent e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteLineAsync(RawText(e.RawMessage)).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string RawText(ReadOnlyMemory<byte> raw) =>
        LossyUtf8.GetString(raw.Span).Replace('\0', '�').Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace('\n', ' ');

    private static string CsvCell(string value)
    {
        bool mustQuote = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);

        if (!mustQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
