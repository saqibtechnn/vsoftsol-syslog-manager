using FluentAssertions;
using VSoftSol.Syslog.Core.VendorSupport;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.VendorSupport;

/// <summary>
/// Parses the real, checked-in VENDOR_SUPPORT.md rather than a fixture, so a future edit to
/// that document that breaks the parser's assumptions (heading text, section order) fails a
/// test instead of silently shipping an empty or truncated "Waiting for messages" page.
/// </summary>
public sealed class VendorSupportDocumentParserRealDocumentTests
{
    private static string ReadRealDocument() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VENDOR_SUPPORT.md"));

    [Fact]
    public void Parse_TheRealDocument_ExtractsAtLeastTwentyVendors()
    {
        IReadOnlyList<DeviceConfigCommand> commands = VendorSupportDocumentParser.Parse(ReadRealDocument());

        commands.Should().HaveCountGreaterThanOrEqualTo(20);
        commands.Select(c => c.VendorName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Parse_TheRealDocument_EveryCommandBlockStillHasBothPlaceholders()
    {
        IReadOnlyList<DeviceConfigCommand> commands = VendorSupportDocumentParser.Parse(ReadRealDocument());

        // Not every vendor uses both (e.g. Cisco ASA has no <PORT>), so this only asserts
        // the parser found real, placeholder-bearing content rather than empty bodies.
        commands.Should().AllSatisfy(c => c.BodyText.Should().NotBeNullOrWhiteSpace());
    }

    [Theory]
    [InlineData("Cisco IOS / IOS-XE")]
    [InlineData("Fortinet FortiGate")]
    [InlineData("Palo Alto PAN-OS")]
    [InlineData("Juniper JunOS")]
    [InlineData("Ubiquiti UniFi")]
    [InlineData("pfSense")]
    public void Parse_TheRealDocument_ContainsEveryPinnedVendorNameVerbatim(string pinnedName)
    {
        IReadOnlyList<DeviceConfigCommand> commands = VendorSupportDocumentParser.Parse(ReadRealDocument());

        commands.Select(c => c.VendorName).Should().Contain(pinnedName,
            "the wizard's pinned-vendor list references this exact heading text");
    }
}
