using System.Text;
using System.Text.Json;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Reporting.Export;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Search;

/// <summary>
/// PHASE_05 export: streamed output matches the grid contents exactly; CSV formula
/// injection is neutralised in the export only; JSON output is HTML-safe (stored-XSS
/// surface).
/// </summary>
public sealed class SearchExportWriterTests
{
    private static SyslogEvent Evt(string message, params EventField[] fields) => new()
    {
        EventId = 1,
        ReceivedUtc = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
        SourceIp = "10.0.0.1",
        Hostname = "h1",
        AppName = "sshd",
        Facility = Facility.SecurityAuth,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes("<13>" + message),
        ParseStatus = ParseStatus.Rfc3164,
        Fields = fields,
    };

    private static async IAsyncEnumerable<SyslogEvent> Stream(params SyslogEvent[] events)
    {
        foreach (SyslogEvent e in events)
        {
            yield return e;
            await Task.Yield();
        }
    }

    private static async Task<string> WriteAsync(ExportFormat format, params SyslogEvent[] events)
    {
        await using var sw = new StringWriter();
        await SearchExportWriter.WriteAsync(format, sw, Stream(events), CancellationToken.None);
        return sw.ToString();
    }

    [Fact]
    public async Task Csv_WritesHeaderThenOneRowPerEvent()
    {
        string csv = await WriteAsync(ExportFormat.Csv, Evt("first"), Evt("second"));

        string[] lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("event_id,received_utc");
        lines[1].Should().Contain("first");
        lines[2].Should().Contain("second");
    }

    [Theory]
    [InlineData("=1+2")]
    [InlineData("+1234567")]
    [InlineData("-2-3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tTAB")]
    public async Task Csv_NeutralisesFormulaInjectionInCells(string dangerous)
    {
        CsvFormulaGuard.NeedsGuard(dangerous).Should().BeTrue();
        CsvFormulaGuard.Guard(dangerous).Should().Be("'" + dangerous);

        string csv = await WriteAsync(ExportFormat.Csv, Evt(dangerous));
        string dataLine = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1];

        // The guarded value ('=1+2 etc.) appears; the bare formula start does not sit at a cell boundary.
        dataLine.Should().Contain("'" + dangerous.Replace("\"", "\"\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("normal text")]
    [InlineData("10.0.0.1")]
    [InlineData("user=root")]
    public void Csv_DoesNotGuardBenignCells(string benign)
    {
        CsvFormulaGuard.NeedsGuard(benign).Should().BeFalse();
        CsvFormulaGuard.Guard(benign).Should().Be(benign);
    }

    [Fact]
    public async Task Csv_QuotesCellsContainingDelimiters()
    {
        string csv = await WriteAsync(ExportFormat.Csv, Evt("has, comma and \"quote\""));

        csv.Should().Contain("\"has, comma and \"\"quote\"\"\"");
    }

    [Fact]
    public async Task Json_IsValidArrayAndRoundTripsFields()
    {
        string json = await WriteAsync(ExportFormat.Json,
            Evt("hello", new EventField("user", "root")),
            Evt("world"));

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("message").GetString().Should().Be("hello");
        doc.RootElement[0].GetProperty("fields")[0].GetProperty("name").GetString().Should().Be("user");
    }

    [Fact]
    public async Task Json_EncodesHtmlSoAStoredXssPayloadCannotBreakOut()
    {
        const string payload = "<script>alert(document.cookie)</script>";
        string json = await WriteAsync(ExportFormat.Json, Evt(payload, new EventField("x", "\"><img src=x onerror=alert(1)>")));

        // Raw markup characters must be unicode-escaped in the serialized text …
        json.Should().NotContain("<script>");
        json.Should().NotContain("<img");
        json.Should().Contain("\\u003C");

        // … but the decoded value is still the exact original (no data loss).
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement[0].GetProperty("message").GetString().Should().Be(payload);
    }

    [Fact]
    public async Task RawText_WritesTheRawMessageOfEachEvent()
    {
        string raw = await WriteAsync(ExportFormat.RawText, Evt("alpha"), Evt("beta"));

        string[] lines = raw.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Be("<13>alpha");
        lines[1].Should().Be("<13>beta");
    }

    [Fact]
    public async Task WriteAsync_ReturnsTheRowCount()
    {
        await using var sw = new StringWriter();
        int count = await SearchExportWriter.WriteAsync(ExportFormat.Csv, sw,
            Stream(Evt("a"), Evt("b"), Evt("c")), CancellationToken.None);

        count.Should().Be(3);
    }
}
