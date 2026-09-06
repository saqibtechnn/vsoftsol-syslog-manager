using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>
/// PHASE_03 Security Validation — memory exhaustion: deeply nested structured data, 10,000
/// SD elements, a single 1MB field, 100k event_fields from one message must all hit a
/// configured cap with bounded allocation.
/// </summary>
[Trait("Category", "Parsing")]
[Trait("Category", "Security")]
public sealed class MemoryBoundsSecurityTests
{
    private static SyslogEvent Parse(string wire, ParsingOptions? options = null) =>
        ParsingComposition.Build(options).Parser.Parse(new RawFrame(
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "203.0.113.10", "udp:test",
            Protocol.Udp, Encoding.UTF8.GetBytes(wire), truncated: false));

    [Fact]
    public void AMessageEngineeredForManyFields_IsCappedAtMaxFieldsPerMessage()
    {
        var options = new ParsingOptions { MaxFieldsPerMessage = 50 };

        // FortiGate KV with 5,000 distinct keys.
        var sb = new StringBuilder("<13>date=x devid=FG ");
        for (int i = 0; i < 5_000; i++)
        {
            sb.Append('k').Append(i).Append("=v ");
        }

        SyslogEvent e = Parse(sb.ToString(), options);

        e.Fields.Count.Should().BeLessThanOrEqualTo(52, "capped at MaxFieldsPerMessage plus a couple of meta fields");
        e.Fields.Should().Contain(f => f.Name == "fields_truncated" && f.Value == "true");
    }

    [Fact]
    public void ASingleEnormousFieldValue_IsClampedToMaxFieldValueLength()
    {
        var options = new ParsingOptions { MaxFieldValueLength = 256 };
        string wire = "<13>date=x devid=FG payload=" + new string('A', 1_000_000);

        SyslogEvent e = Parse(wire, options);

        EventField payload = e.Fields.Single(f => f.Name == "payload");
        payload.Value.Length.Should().Be(256);
    }

    [Fact]
    public void TenThousandStructuredDataElements_DoNotProduceTenThousandFields_NorHang()
    {
        var options = new ParsingOptions { MaxMessageChars = 512 * 1024 };
        var sb = new StringBuilder("<165>1 2026-03-01T10:00:00Z host app - - ");
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("[e").Append(i).Append("@1 a=\"1\"]");
        }

        sb.Append(" x");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SyslogEvent e = Parse(sb.ToString(), options);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        e.ParseStatus.Should().Be(ParseStatus.Rfc5424);
        // SD is serialised into structured_data_json (bounded string), not exploded into event_fields.
        e.Fields.Count.Should().BeLessThan(20);
    }

    [Fact]
    public void DecodedMessageOverTheCharCap_IsTruncatedWithAFlag_NotStoredInFull()
    {
        var options = new ParsingOptions { MaxMessageChars = 1_000 };
        string wire = "<13>Mar  1 10:00:00 host app: " + new string('Z', 500_000);

        SyslogEvent e = Parse(wire, options);

        e.Message.Length.Should().BeLessThanOrEqualTo(1_000);
        e.Fields.Should().Contain(f => f.Name == "truncated" && f.Value == "true");
        e.RawMessage.Length.Should().Be(Encoding.UTF8.GetByteCount(wire), "raw bytes are still kept in full");
    }
}
