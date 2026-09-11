using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>
/// Data-driven over <c>tests/fixtures/messages/&lt;vendor&gt;/corpus.jsonl</c> (PHASE_03:
/// "field-by-field assertion for every fixture", "raw_message byte-identical for 100% of
/// fixtures", "≥ 10 per vendor, ≥ 200 total across the core eight").
/// </summary>
[Trait("Category", "Parsing")]
public sealed class VendorFixtureTests
{
    private static readonly MessageParser Parser = ParsingComposition.Build().Parser;

    public static TheoryData<string, int> Fixtures()
    {
        var data = new TheoryData<string, int>();
        foreach (string file in FixtureFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    data.Add(file, i);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_ParsesToItsExpectedFields_AndKeepsRawBytesVerbatim(string file, int lineNumber)
    {
        string line = File.ReadAllLines(file)[lineNumber];
        Fixture f = JsonSerializer.Deserialize<Fixture>(line, JsonOpts)!;
        f.Wire.Should().NotBeNull("fixture {0}:{1} has no 'wire'", file, lineNumber);

        byte[] wireBytes = Encoding.UTF8.GetBytes(f.Wire!);
        var frame = new RawFrame(
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "203.0.113.200", "udp:test",
            Protocol.Udp, wireBytes, truncated: false);

        SyslogEvent e = Parser.Parse(frame);

        e.RawMessage.ToArray().Should().Equal(wireBytes, "raw_message must be byte-identical to the input");

        Enum.Parse<ParseStatus>(f.Status!, ignoreCase: true).Should().Be(e.ParseStatus);

        if (f.Vendor is not null)
        {
            e.Vendor.Should().Be(f.Vendor);
        }

        if (f.Host is not null)
        {
            e.Hostname.Should().Be(f.Host);
        }

        if (f.App is not null)
        {
            e.AppName.Should().Be(f.App);
        }

        if (f.Msg is not null)
        {
            e.Message.Should().Be(f.Msg);
        }

        if (f.EventUtc is not null)
        {
            e.EventUtc.Should().Be(DateTimeOffset.Parse(f.EventUtc, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
        }

        if (f.Fields is not null)
        {
            foreach ((string name, string value) in f.Fields)
            {
                e.Fields.Should().Contain(
                    field => field.Name == name && field.Value == value,
                    "fixture expects field {0}={1}; got [{2}]", name, value,
                    string.Join(", ", e.Fields.Select(x => $"{x.Name}={x.Value}")));
            }
        }
    }

    [Fact]
    public void Corpus_HasAtLeastTenFixturesPerVendor_AndAtLeastTwoHundredTotal()
    {
        int total = 0;
        foreach (string file in FixtureFiles())
        {
            int count = File.ReadAllLines(file).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            count.Should().BeGreaterThanOrEqualTo(10, "vendor corpus {0}", file);
            total += count;
        }

        // Phase 3's core eight (8 packs, >=25 fixtures each) plus Phase 11's extended seven
        // (9 packs — pfSense/OPNsense and both Aruba AOS variants each count separately,
        // >=10 fixtures each per PHASE_11_HARDENING.md) = 17 packs total.
        FixtureFiles().Should().HaveCount(17, "the core eight plus the extended seven (nine packs)");
        total.Should().BeGreaterThanOrEqualTo(290);
    }

    private static IEnumerable<string> FixtureFiles()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "messages");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "corpus.jsonl", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
            : [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record Fixture
    {
        [JsonPropertyName("wire")] public string? Wire { get; init; }

        [JsonPropertyName("status")] public string? Status { get; init; }

        [JsonPropertyName("vendor")] public string? Vendor { get; init; }

        [JsonPropertyName("host")] public string? Host { get; init; }

        [JsonPropertyName("app")] public string? App { get; init; }

        [JsonPropertyName("msg")] public string? Msg { get; init; }

        [JsonPropertyName("event_utc")] public string? EventUtc { get; init; }

        [JsonPropertyName("fields")] public Dictionary<string, string>? Fields { get; init; }
    }
}
