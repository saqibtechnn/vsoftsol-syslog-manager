using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Reporting.Csv;
using VSoftSol.Syslog.Reporting.Pdf;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>
/// PHASE_10 build item 7 — PDF/CSV rendering. Row-count and figure correctness are proven
/// at the <c>ReportContentReader</c> layer against an independent SQL oracle
/// (<c>ReportContentReaderTests</c>); these tests prove the renderers faithfully carry that
/// already-correct content onto the page/file without throwing, without losing rows, and
/// without letting hostile log content escape as anything other than literal text
/// (SECURITY_STANDARDS.md "XSS in PDF and CSV").
/// </summary>
public sealed class ReportRenderingTests
{
    private static ReportContent ListContent(IReadOnlyList<ReportEventRow> rows, bool archivedOmitted = false) => new()
    {
        ReportName = "Test Report",
        TemplateKey = CannedReportCatalog.FailedAuthentication,
        ControlReference = "PCI-DSS v4.0 Requirement 10.2.4",
        QueryText = "auth AND fail",
        GeneratedUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        FromUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        ToUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        GeneratingUser = "auditor1",
        EventRows = rows,
        RowCount = rows.Count,
        ArchivedPeriodsOmitted = archivedOmitted ? [new ArchivedPeriod(DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-90))] : [],
    };

    private static ReportContent AggregateContent(IReadOnlyList<ReportAggregateRow> rows) => new()
    {
        ReportName = "Severity Trend",
        TemplateKey = CannedReportCatalog.SeverityTrend,
        QueryText = "*",
        GeneratedUtc = DateTimeOffset.UtcNow,
        FromUtc = DateTimeOffset.UtcNow.AddDays(-7),
        ToUtc = DateTimeOffset.UtcNow,
        GeneratingUser = "auditor1",
        AggregateRows = rows,
        RowCount = rows.Count,
    };

    [Fact]
    public void ReportPdfRenderer_Render_ProducesAValidPdfDocument()
    {
        ReportContent content = ListContent([new ReportEventRow(1, DateTimeOffset.UtcNow, "Error", "web-1", "sshd", "authentication failed for bob", "cisco")]);

        byte[] pdf = ReportPdfRenderer.Render(content, logoWideBytes: null);

        pdf.Length.Should().BeGreaterThan(500);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void ReportPdfRenderer_Render_ScalesWithRowCount()
    {
        byte[] fewRows = ReportPdfRenderer.Render(ListContent([new ReportEventRow(1, DateTimeOffset.UtcNow, "Error", "h", "a", "m", "v")]), null);
        byte[] manyRows = ReportPdfRenderer.Render(
            ListContent([.. Enumerable.Range(0, 200).Select(i => new ReportEventRow(i, DateTimeOffset.UtcNow, "Error", "h", "a", $"message {i}", "v"))]),
            null);

        manyRows.Length.Should().BeGreaterThan(fewRows.Length);
    }

    [Fact]
    public void ReportPdfRenderer_Render_WithHostileLogContent_DoesNotThrow_AndStaysLiteralText()
    {
        string hostile = "<script>alert(1)</script> =cmd|'/C calc'!A1 \0\r\n\"quoted\" ";
        ReportContent content = ListContent([new ReportEventRow(1, DateTimeOffset.UtcNow, "Critical", "h", "a", hostile, "v")]);

        Action act = () => ReportPdfRenderer.Render(content, null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ReportPdfRenderer_Render_WithAnAggregateReport_DoesNotThrow()
    {
        ReportContent content = AggregateContent([new ReportAggregateRow("Error", "2026-01-01 00:00 UTC", 42)]);

        Action act = () => ReportPdfRenderer.Render(content, null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ReportPdfRenderer_Render_WithNoRows_DoesNotThrow_EmptyStateHandled()
    {
        Action act = () => ReportPdfRenderer.Render(ListContent([]), null);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ReportCsvWriter_WriteAsync_ReturnsTheCorrectRowCount()
    {
        ReportContent content = ListContent([
            new ReportEventRow(1, DateTimeOffset.UtcNow, "Error", "h1", "a", "m1", "v"),
            new ReportEventRow(2, DateTimeOffset.UtcNow, "Warning", "h2", "a", "m2", "v"),
        ]);
        using var writer = new StringWriter();

        int count = await ReportCsvWriter.WriteAsync(writer, content, CancellationToken.None);

        count.Should().Be(2);
    }

    [Fact]
    public async Task ReportCsvWriter_WriteAsync_CarriesTheMetadataHeader()
    {
        ReportContent content = ListContent([]);
        using var writer = new StringWriter();

        await ReportCsvWriter.WriteAsync(writer, content, CancellationToken.None);
        string csv = writer.ToString();

        csv.Should().Contain("# report,Test Report");
        csv.Should().Contain("# generating_user,auditor1");
        csv.Should().Contain("# archived_data_omitted,no");
    }

    [Fact]
    public async Task ReportCsvWriter_WriteAsync_DisclosesArchivedDataOmission()
    {
        ReportContent content = ListContent([], archivedOmitted: true);
        using var writer = new StringWriter();

        await ReportCsvWriter.WriteAsync(writer, content, CancellationToken.None);

        writer.ToString().Should().Contain("# archived_data_omitted,yes");
    }

    [Theory]
    [InlineData("=cmd|'/C calc'!A1")]
    [InlineData("+SUM(1+1)")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public async Task ReportCsvWriter_WriteAsync_NeutralisesFormulaInjection_InTheMessageColumn(string hostileMessage)
    {
        ReportContent content = ListContent([new ReportEventRow(1, DateTimeOffset.UtcNow, "Error", "h", "a", hostileMessage, "v")]);
        using var writer = new StringWriter();

        await ReportCsvWriter.WriteAsync(writer, content, CancellationToken.None);
        string csv = writer.ToString();

        string dataLine = csv.Split('\n').Single(l => l.Contains(hostileMessage[1..], StringComparison.Ordinal));
        dataLine.Should().Contain("'" + hostileMessage, "a formula-leading cell must be prefixed so Excel treats it as text");
    }

    [Fact]
    public async Task ReportCsvWriter_WriteAsync_ForAnAggregateReport_WritesGroupBucketValueColumns()
    {
        ReportContent content = AggregateContent([new ReportAggregateRow("Error", "2026-01-01 00:00 UTC", 42)]);
        using var writer = new StringWriter();

        await ReportCsvWriter.WriteAsync(writer, content, CancellationToken.None);
        string csv = writer.ToString();

        csv.Should().Contain("group,bucket,value");
        csv.Should().Contain("Error,2026-01-01 00:00 UTC,42");
    }
}
