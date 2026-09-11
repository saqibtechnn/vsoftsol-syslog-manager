using FluentAssertions;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.Ingestion.Patterns;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class PatternPackLoaderTests
{
    [Fact]
    public void Load_TheCoreEightPlusTheExtendedSeven_AreAllPresentAndCompileWithoutError()
    {
        // Widened in Phase 11: the extended seven (PHASE_11_HARDENING.md) ship as nine
        // packs — pfSense/OPNsense and both Aruba AOS variants each count separately —
        // alongside the Phase 3 core eight, for 17 total.
        (MessageParser _, DeduplicationWindow _2) = ParsingComposition.Build();
        var loader = new PatternPackLoader(
            Microsoft.Extensions.Options.Options.Create(new ParsingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PatternPackLoader>.Instance);

        IReadOnlyList<PatternPack> packs = loader.Load();

        packs.Select(p => p.Vendor).Should().BeEquivalentTo(
            "cisco-ios", "cisco-asa", "fortigate", "paloalto",
            "juniper-junos", "mikrotik-routeros", "ubiquiti-unifi", "linux",
            "checkpoint-gaia", "sophos-xg", "sonicwall-sonicos", "pfsense", "opnsense",
            "aruba-aos-switch", "aruba-aos-cx", "huawei-vrp", "vmware-esxi");

        packs.Should().OnlyContain(p => p.MatchRules.Count > 0, "every pack must be auto-detectable");
        packs.Should().OnlyContain(p => p.Pipeline.StageCount > 0);
    }

    [Fact]
    public void Load_MalformedPack_IsSkipped_NotFatal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vsoftsol-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bad"));
        File.WriteAllText(Path.Combine(dir, "bad", "bad.pack"), "[grok]\n(unclosed group %{NOPE}\n");
        File.WriteAllText(Path.Combine(dir, "bad", "good.pack"), "[pack]\nvendor = ok\n[match]\nmessage ~ ok\n[grok]\n(?<x>ok)\n");

        try
        {
            var loader = new PatternPackLoader(
                Microsoft.Extensions.Options.Options.Create(new ParsingOptions { PatternsDirectory = dir }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PatternPackLoader>.Instance);

            IReadOnlyList<PatternPack> packs = loader.Load();

            packs.Select(p => p.Vendor).Should().ContainSingle().Which.Should().Be("ok");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
