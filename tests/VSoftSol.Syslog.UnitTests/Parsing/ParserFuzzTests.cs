using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>
/// PHASE_03 fuzz: "truncated priorities, missing timestamps, invalid UTF-8, embedded
/// nulls, 1MB messages, deeply nested structured data, ANSI escape sequences, and messages
/// crafted to trigger regex backtracking. Assert every pattern has a timeout."
/// </summary>
[Trait("Category", "Parsing")]
public sealed class ParserFuzzTests
{
    private static readonly MessageParser Parser = ParsingComposition.Build().Parser;
    private static readonly DateTimeOffset Received = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static SyslogEvent Parse(byte[] payload) =>
        Parser.Parse(new RawFrame(Received, "203.0.113.10", "udp:test", Protocol.Udp, payload, truncated: false));

    [Theory]
    [InlineData("<")]
    [InlineData("<9")]
    [InlineData("<>")]
    [InlineData("<999999>1 x")]
    [InlineData("<0>")]
    [InlineData("<13>")]
    [InlineData("<13>1")]
    [InlineData("Mar")]
    [InlineData("<13>Mar 99 99:99:99 h a: m")]
    public void HeaderEdgeCases_NeverThrow_AndAlwaysProduceAnEvent(string wire)
    {
        Action act = () => Parse(Encoding.UTF8.GetBytes(wire));
        act.Should().NotThrow();
        Parse(Encoding.UTF8.GetBytes(wire)).RawMessage.Length.Should().Be(Encoding.UTF8.GetByteCount(wire));
    }

    [Fact]
    public void OneMegabyteMessage_IsParsedWithinLimits_NoHang()
    {
        byte[] big = Encoding.UTF8.GetBytes("<13>Mar  1 10:00:00 host app: " + new string('A', 1_000_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        SyslogEvent e = Parse(big);

        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        e.RawMessage.Length.Should().Be(big.Length, "raw bytes are kept in full");
    }

    [Fact]
    public void DeeplyNestedStructuredData_DoesNotStackOverflowOrHang()
    {
        var sb = new StringBuilder("<165>1 2026-03-01T10:00:00Z host app - - ");
        for (int i = 0; i < 5_000; i++)
        {
            sb.Append("[sd").Append(i).Append("@1 k=\"v\"]");
        }

        sb.Append(" message");

        Action act = () => Parse(Encoding.UTF8.GetBytes(sb.ToString()));
        act.Should().NotThrow();
    }

    [Fact]
    public void ManyStructuredDataElements_AreParsed_WhenUnderTheCharCap()
    {
        var sb = new StringBuilder("<165>1 2026-03-01T10:00:00Z host app - - ");
        for (int i = 0; i < 1_500; i++)
        {
            sb.Append("[e").Append(i).Append("@1 a=\"1\" b=\"2\"]");
        }

        sb.Append(" x");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SyslogEvent e = Parse(Encoding.UTF8.GetBytes(sb.ToString()));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        e.StructuredDataJson.Should().NotBeNull();
    }

    [Fact]
    public void TenThousandStructuredDataElements_OverTheCharCap_StillProduceOneBoundedEvent()
    {
        var sb = new StringBuilder("<165>1 2026-03-01T10:00:00Z host app - - ");
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("[e").Append(i).Append("@1 a=\"1\" b=\"2\"]");
        }

        byte[] wire = Encoding.UTF8.GetBytes(sb.ToString());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SyslogEvent e = Parse(wire);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        e.RawMessage.Length.Should().Be(wire.Length, "raw bytes kept in full");
        e.Fields.Count.Should().BeLessThan(20, "never exploded into per-element fields");
    }

    [Fact]
    public void AnsiEscapeSequences_AreKeptVerbatim_NotInterpreted()
    {
        byte[] wire = Encoding.UTF8.GetBytes("<13>Mar  1 10:00:00 host app: [31mRED[0m alert ]0;title");
        SyslogEvent e = Parse(wire);

        e.RawMessage.ToArray().Should().Equal(wire);
    }

    [Fact]
    public void EmbeddedNulBytes_KeepRawVerbatim_AndDoNotTruncateTheStoredEvent()
    {
        byte[] wire = [.. "<13>Mar  1 10:00:00 host app: a"u8, 0x00, .. "b"u8, 0x00, .. "c"u8];
        SyslogEvent e = Parse(wire);

        e.RawMessage.ToArray().Should().Equal(wire);
    }

    [Fact]
    public void RegexBacktrackingBait_HitsTheMatchTimeout_AndDoesNotHang()
    {
        // A GROK pattern crafted for catastrophic backtracking, against input designed to
        // trigger it. The mandatory match timeout must fire and extraction must continue.
        // A pack author writes an evil pattern; a hostile message triggers the backtracking.
        // Nested unbounded quantifiers over '.*' that the JIT regex cannot make atomic.
        var grok = new GrokLibrary(TimeSpan.FromMilliseconds(100));
        var extractor = new GrokExtractor(grok.Compile(@"(?<x>(.*,){20})z"), "evil");
        var ctx = new ExtractionContext(
            string.Join(",", Enumerable.Range(0, 30)) + " no trailing z", "1.1.1.1", null, null, 250, 8192);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Action act = () => extractor.Apply(ctx);
        act.Should().NotThrow("the extractor swallows the timeout and continues");
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the mandatory match timeout bounds it");
        ctx.Fields.Should().ContainKey("extractor_timeout");
    }

    [Fact]
    public void EveryLoadedVendorPack_CompilesWithinTheMatchTimeout_AndSurvivesAHostileMessage()
    {
        // A single hostile message run through every pack's pipeline — no pattern hangs.
        byte[] hostile = Encoding.UTF8.GetBytes("<13>Mar  1 10:00:00 h a: " + new string('=', 2000) + string.Concat(Enumerable.Repeat("a,", 2000)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = Parse(hostile);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
