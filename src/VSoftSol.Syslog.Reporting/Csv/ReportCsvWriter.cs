using System.Globalization;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Reporting.Export;

namespace VSoftSol.Syslog.Reporting.Csv;

/// <summary>
/// Renders a resolved <see cref="ReportContent"/> to CSV (PHASE_10 build item 7). A metadata
/// header carries product/version, generation timestamp, time range, the query used, the
/// generating user, and whether archived data was omitted (BRANDING.md; the phase's
/// "whether archived data was included" requirement). Every data cell goes through
/// <see cref="CsvFormulaGuard"/>, the same defence <c>SearchExportWriter</c> already applies
/// (SECURITY_STANDARDS.md "CSV / formula injection").
/// </summary>
public static class ReportCsvWriter
{
    public static async Task<int> WriteAsync(TextWriter writer, ReportContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(content);

        await WriteMetaAsync(writer, content, cancellationToken).ConfigureAwait(false);

        if (content.EventRows.Count > 0)
        {
            return await WriteRowsAsync(
                writer, ["received_utc", "severity", "host", "app", "message"],
                content.EventRows.Select(r => new[]
                {
                    r.ReceivedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    r.Severity, r.Host ?? string.Empty, r.App ?? string.Empty, r.Message,
                }),
                cancellationToken).ConfigureAwait(false);
        }

        if (content.AggregateRows.Count > 0)
        {
            return await WriteRowsAsync(
                writer, ["group", "bucket", "value"],
                content.AggregateRows.Select(r => new[]
                {
                    r.Group ?? "(none)", r.BucketLabel ?? string.Empty, r.Value.ToString(CultureInfo.InvariantCulture),
                }),
                cancellationToken).ConfigureAwait(false);
        }

        return await WriteRowsAsync(
            writer, ["occurred_utc", "actor", "action", "entity_type", "entity_id", "detail"],
            content.AuditRows.Select(r => new[]
            {
                r.OccurredUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                r.Actor ?? "system", r.Action, r.EntityType ?? string.Empty, r.EntityId ?? string.Empty, r.Detail ?? string.Empty,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteMetaAsync(TextWriter writer, ReportContent content, CancellationToken cancellationToken)
    {
        string version = typeof(ReportCsvWriter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        await writer.WriteLineAsync($"# {Cell(BrandingInfo.ProductName)},{Cell(version)}").ConfigureAwait(false);
        await writer.WriteLineAsync($"# report,{Cell(content.ReportName)}").ConfigureAwait(false);
        await writer.WriteLineAsync($"# generated_utc,{Cell(content.GeneratedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}").ConfigureAwait(false);
        await writer.WriteLineAsync(
            $"# range_utc,{Cell(content.FromUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))} to {Cell(content.ToUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}")
            .ConfigureAwait(false);
        await writer.WriteLineAsync($"# query,{Cell(content.QueryText)}").ConfigureAwait(false);
        await writer.WriteLineAsync($"# generating_user,{Cell(content.GeneratingUser)}").ConfigureAwait(false);
        await writer.WriteLineAsync($"# archived_data_omitted,{Cell(content.HasArchivedDataOmitted ? "yes" : "no")}").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<int> WriteRowsAsync(TextWriter writer, string[] columns, IEnumerable<string[]> rows, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(string.Join(',', columns.Select(Cell))).ConfigureAwait(false);

        int count = 0;
        foreach (string[] row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',', row.Select(v => Cell(CsvFormulaGuard.Guard(v))))).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static string Cell(string value)
    {
        bool mustQuote = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);

        return mustQuote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
