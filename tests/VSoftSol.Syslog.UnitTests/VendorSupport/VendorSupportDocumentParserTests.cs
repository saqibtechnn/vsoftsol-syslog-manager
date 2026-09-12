using FluentAssertions;
using VSoftSol.Syslog.Core.VendorSupport;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.VendorSupport;

/// <summary>PHASE_12 build item 2a — the "Waiting for messages" page's copy-ready device
/// commands are parsed straight out of VENDOR_SUPPORT.md so the two can never drift.</summary>
public sealed class VendorSupportDocumentParserTests
{
    private const string Fixture = """
        # Vendor support

        ## The three support tiers

        Some unrelated prose that must not leak into the parsed commands.

        ## Device configuration commands

        These are the copy-ready blocks for the "Waiting for messages" page and the User Guide.

        ### Cisco IOS / IOS-XE
        ```
        configure terminal
         logging host <SYSLOG_IP> transport udp port <PORT>
         logging trap informational
        end
        ```

        ### Palo Alto PAN-OS
        GUI: **Device -> Server Profiles -> Syslog -> Add** - name it, add a server with
        `<SYSLOG_IP>`, port `<PORT>`, transport UDP, format BSD.

        ## Parser pack roadmap

        Unrelated trailing section that must not be parsed as a device.
        """;

    [Fact]
    public void Parse_TheDeviceConfigurationSection_ExtractsOnlyItsSubsections()
    {
        IReadOnlyList<DeviceConfigCommand> commands = VendorSupportDocumentParser.Parse(Fixture);

        commands.Should().HaveCount(2);
        commands.Select(c => c.VendorName).Should().Equal("Cisco IOS / IOS-XE", "Palo Alto PAN-OS");
    }

    [Fact]
    public void Parse_AFencedCommandBlock_IsMarkedAsACommandBlockAndKeepsPlaceholders()
    {
        DeviceConfigCommand cisco = VendorSupportDocumentParser.Parse(Fixture).Single(c => c.VendorName == "Cisco IOS / IOS-XE");

        cisco.IsCommandBlock.Should().BeTrue();
        cisco.BodyText.Should().Contain("<SYSLOG_IP>").And.Contain("<PORT>");
        cisco.BodyText.Should().NotContain("```", "the fence markers themselves are not part of the copy-ready text");
    }

    [Fact]
    public void Parse_ProseWithoutAFence_IsNotMarkedAsACommandBlock()
    {
        DeviceConfigCommand paloAlto = VendorSupportDocumentParser.Parse(Fixture).Single(c => c.VendorName == "Palo Alto PAN-OS");

        paloAlto.IsCommandBlock.Should().BeFalse();
        paloAlto.BodyText.Should().Contain("<SYSLOG_IP>");
    }

    [Fact]
    public void Render_GivenAnIpAndPort_SubstitutesOnlyTheKnownPlaceholders()
    {
        DeviceConfigCommand cisco = VendorSupportDocumentParser.Parse(Fixture).Single(c => c.VendorName == "Cisco IOS / IOS-XE");

        string rendered = cisco.Render("10.0.0.5", 514);

        rendered.Should().Contain("10.0.0.5").And.Contain("port 514").And.NotContain("<SYSLOG_IP>").And.NotContain("<PORT>");
    }

    [Fact]
    public void PinnedFirst_Always_OrdersThePinnedVendorsBeforeEverythingElseAndKeepsTheRest()
    {
        IReadOnlyList<DeviceConfigCommand> all = VendorSupportDocumentParser.Parse(Fixture);

        IReadOnlyList<DeviceConfigCommand> ordered = VendorSupportDocumentParser.PinnedFirst(all, ["Palo Alto PAN-OS"]);

        ordered.Should().HaveCount(2);
        ordered[0].VendorName.Should().Be("Palo Alto PAN-OS");
    }
}
