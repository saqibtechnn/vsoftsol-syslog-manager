using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Reports;

namespace VSoftSol.Syslog.Reporting.Pdf;

/// <summary>
/// Renders a resolved <see cref="ReportContent"/> to PDF (PHASE_10 build item 7). Takes
/// already-resolved data and (optionally) the branding logo bytes — it runs no query of its
/// own and touches no filesystem, matching the existing <c>SearchExportWriter</c> precedent
/// ("Reporting" renders what the caller hands it). Every value reaches the page only through
/// QuestPDF's <c>Text()</c> API, which draws literal glyphs, not markup — hostile log content
/// (script tags, formula-leading characters, control bytes) can never become anything other
/// than text on the page (SECURITY_STANDARDS.md "XSS in PDF and CSV").
/// </summary>
public static class ReportPdfRenderer
{
    static ReportPdfRenderer() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] Render(ReportContent content, byte[]? logoWideBytes)
    {
        ArgumentNullException.ThrowIfNull(content);

        QuestPDF.Fluent.Document document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(h => ComposeHeader(h, content, logoWideBytes));
                page.Content().Element(c => ComposeBody(c, content));
                page.Footer().Element(ComposeFooter);
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, ReportContent content, byte[]? logoWideBytes)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(title =>
                {
                    title.Item().Text(content.ReportName).FontSize(16).Bold();
                    if (content.ControlReference is { } control)
                    {
                        title.Item().Text(control).FontSize(9).Italic();
                    }
                });

                if (logoWideBytes is { Length: > 0 })
                {
                    row.ConstantItem(140).Image(logoWideBytes).FitWidth();
                }
            });

            col.Item().PaddingTop(6).Text(text =>
            {
                text.Span("Generated ").FontSize(8);
                text.Span(content.GeneratedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).FontSize(8).SemiBold();
                text.Span("  ·  Range ").FontSize(8);
                text.Span($"{content.FromUtc.UtcDateTime:yyyy-MM-dd} to {content.ToUtc.UtcDateTime:yyyy-MM-dd} UTC").FontSize(8).SemiBold();
                text.Span("  ·  By ").FontSize(8);
                text.Span(content.GeneratingUser).FontSize(8).SemiBold();
            });

            col.Item().Text(text =>
            {
                text.Span("Query: ").FontSize(8);
                text.Span(content.QueryText).FontSize(8).FontFamily("Consolas");
            });

            if (content.HasArchivedDataOmitted)
            {
                col.Item().PaddingTop(2).Text(
                    $"Note: {content.ArchivedPeriodsOmitted.Count} archived period(s) within this range are not " +
                    "included below — restore the relevant archive(s) before reporting for complete coverage.")
                    .FontSize(8).Italic();
            }

            if (content.Truncated)
            {
                col.Item().PaddingTop(2).Text("Note: this report hit its row cap — figures are a lower bound.").FontSize(8).Italic();
            }

            col.Item().PaddingTop(6).LineHorizontal(0.5f);
        });
    }

    private static void ComposeBody(IContainer container, ReportContent content)
    {
        if (content.EventRows.Count > 0)
        {
            ComposeEventTable(container, content.EventRows);
        }
        else if (content.AggregateRows.Count > 0)
        {
            ComposeAggregateTable(container, content.AggregateRows);
        }
        else if (content.AuditRows.Count > 0)
        {
            ComposeAuditTable(container, content.AuditRows);
        }
        else
        {
            container.Text("No matching data in this time range.").Italic();
        }
    }

    private static void ComposeEventTable(IContainer container, IReadOnlyList<ReportEventRow> rows)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(110);
                c.ConstantColumn(45);
                c.RelativeColumn(2);
                c.RelativeColumn(2);
                c.RelativeColumn(5);
            });

            table.Header(h =>
            {
                foreach (string label in new[] { "Time (UTC)", "Severity", "Host", "App", "Message" })
                {
                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(label).Bold();
                }
            });

            foreach (ReportEventRow row in rows)
            {
                table.Cell().Text(row.ReceivedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                table.Cell().Text(row.Severity);
                table.Cell().Text(row.Host ?? string.Empty);
                table.Cell().Text(row.App ?? string.Empty);
                table.Cell().Text(row.Message);
            }
        });
    }

    private static void ComposeAggregateTable(IContainer container, IReadOnlyList<ReportAggregateRow> rows)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(3);
                c.RelativeColumn(2);
                c.RelativeColumn(1);
            });

            table.Header(h =>
            {
                foreach (string label in new[] { "Group", "Bucket", "Value" })
                {
                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(label).Bold();
                }
            });

            foreach (ReportAggregateRow row in rows)
            {
                table.Cell().Text(row.Group ?? "(none)");
                table.Cell().Text(row.BucketLabel ?? string.Empty);
                table.Cell().Text(row.Value.ToString("N0", CultureInfo.InvariantCulture));
            }
        });
    }

    private static void ComposeAuditTable(IContainer container, IReadOnlyList<ReportAuditRow> rows)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(110);
                c.RelativeColumn(2);
                c.RelativeColumn(2);
                c.RelativeColumn(2);
                c.RelativeColumn(4);
            });

            table.Header(h =>
            {
                foreach (string label in new[] { "When (UTC)", "Actor", "Action", "Entity", "Detail" })
                {
                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(label).Bold();
                }
            });

            foreach (ReportAuditRow row in rows)
            {
                table.Cell().Text(row.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                table.Cell().Text(row.Actor ?? "system");
                table.Cell().Text(row.Action);
                table.Cell().Text(row.EntityType is null ? string.Empty : $"{row.EntityType} {row.EntityId}");
                table.Cell().Text(row.Detail ?? string.Empty);
            }
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.DefaultTextStyle(x => x.FontSize(7)).Row(row =>
        {
            row.RelativeItem().Text(BrandingInfo.Copyright);
            row.RelativeItem().AlignRight().Text(text =>
            {
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }
}
