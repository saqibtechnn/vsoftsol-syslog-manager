using FluentAssertions;
using VSoftSol.Syslog.Core.Snmp;
using VSoftSol.Syslog.UnitTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Snmp;

[Trait("Category", "Snmp")]
public sealed class SnmpBerReaderTests
{
    private static byte[] BuildV1Trap(string community = "public") =>
        BerBuilder.Sequence(0x30,
            BerBuilder.Integer(0), // version = v1
            BerBuilder.OctetString(community),
            BerBuilder.Sequence(0xA4, // Trap-PDU
                BerBuilder.Oid("1.3.6.1.4.1.9.1.1"), // enterprise
                BerBuilder.IpAddress(10, 0, 0, 1), // agent-addr
                BerBuilder.Integer(2), // generic-trap = linkDown
                BerBuilder.Integer(0), // specific-trap
                BerBuilder.UnsignedApp(0x43, 12345), // time-stamp
                BerBuilder.Sequence(0x30, // varbind list
                    BerBuilder.Sequence(0x30,
                        BerBuilder.Oid("1.3.6.1.2.1.2.2.1.1.5"),
                        BerBuilder.OctetString("eth0")))));

    private static byte[] BuildV2cTrap() =>
        BerBuilder.Sequence(0x30,
            BerBuilder.Integer(1), // version = v2c
            BerBuilder.OctetString("public"),
            BerBuilder.Sequence(0xA7, // SNMPv2-Trap-PDU
                BerBuilder.Integer(1), // request-id
                BerBuilder.Integer(0), // error-status
                BerBuilder.Integer(0), // error-index
                BerBuilder.Sequence(0x30,
                    BerBuilder.Sequence(0x30,
                        BerBuilder.Oid("1.3.6.1.2.1.1.3.0"), // sysUpTime.0
                        BerBuilder.UnsignedApp(0x43, 500)),
                    BerBuilder.Sequence(0x30,
                        BerBuilder.Oid("1.3.6.1.6.3.1.1.4.1.0"), // snmpTrapOID.0
                        BerBuilder.Oid("1.3.6.1.6.3.1.1.5.3")), // linkDown well-known OID
                    BerBuilder.Sequence(0x30,
                        BerBuilder.Oid("1.3.6.1.2.1.2.2.1.1.5"),
                        BerBuilder.Integer(5)))));

    [Fact]
    public void TryParse_V1LinkDownTrap_DecodesEveryField()
    {
        bool ok = SnmpBerReader.TryParse(BuildV1Trap(), out SnmpTrapMessage? msg, out string? error);

        ok.Should().BeTrue(error);
        msg!.Version.Should().Be(SnmpVersion.V1);
        msg.Community.Should().Be("public");
        msg.EnterpriseOid.Should().Be("1.3.6.1.4.1.9.1.1");
        msg.AgentAddress.Should().Be("10.0.0.1");
        msg.GenericTrap.Should().Be(2);
        msg.SpecificTrap.Should().Be(0);
        msg.UptimeTicks.Should().Be(12345);
        msg.Varbinds.Should().ContainSingle()
            .Which.Should().Be(new SnmpVarbind("1.3.6.1.2.1.2.2.1.1.5", SnmpVarbindKind.OctetString, "eth0"));
    }

    [Fact]
    public void TryParse_V2cTrap_ExtractsUptimeAndTrapOidFromVarbinds()
    {
        bool ok = SnmpBerReader.TryParse(BuildV2cTrap(), out SnmpTrapMessage? msg, out string? error);

        ok.Should().BeTrue(error);
        msg!.Version.Should().Be(SnmpVersion.V2c);
        msg.UptimeTicks.Should().Be(500);
        msg.TrapOid.Should().Be("1.3.6.1.6.3.1.1.5.3");
        msg.Varbinds.Should().HaveCount(3);
        msg.Varbinds[2].Kind.Should().Be(SnmpVarbindKind.Integer);
        msg.Varbinds[2].DisplayValue.Should().Be("5");
    }

    [Fact]
    public void TryParse_TruncatedDatagram_FailsWithoutThrowing()
    {
        byte[] full = BuildV1Trap();
        byte[] truncated = full[..(full.Length - 5)];

        bool ok = SnmpBerReader.TryParse(truncated, out SnmpTrapMessage? msg, out string? error);

        ok.Should().BeFalse();
        msg.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryParse_UnsupportedVersion_IsRejected()
    {
        byte[] frame = BerBuilder.Sequence(0x30,
            BerBuilder.Integer(99),
            BerBuilder.OctetString("public"),
            BerBuilder.Sequence(0xA4, BerBuilder.Null()));

        bool ok = SnmpBerReader.TryParse(frame, out SnmpTrapMessage? msg, out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("version");
    }

    [Fact]
    public void TryParse_OversizedOid_IsRejectedWithoutAllocatingUnbounded()
    {
        byte[] frame = BerBuilder.Sequence(0x30,
            BerBuilder.Integer(0),
            BerBuilder.OctetString("public"),
            BerBuilder.Sequence(0xA4,
                BerBuilder.OversizedOid(500), // far beyond DefaultMaxOidArcs
                BerBuilder.IpAddress(1, 2, 3, 4),
                BerBuilder.Integer(0),
                BerBuilder.Integer(0),
                BerBuilder.UnsignedApp(0x43, 1),
                BerBuilder.Sequence(0x30)));

        bool ok = SnmpBerReader.TryParse(frame, out SnmpTrapMessage? msg, out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("OID");
    }

    [Fact]
    public void TryParse_MalformedVarbind_IsRejectedWithoutThrowing()
    {
        // A VarBind whose "name" is an INTEGER instead of an OID.
        byte[] frame = BerBuilder.Sequence(0x30,
            BerBuilder.Integer(0),
            BerBuilder.OctetString("public"),
            BerBuilder.Sequence(0xA4,
                BerBuilder.Oid("1.3.6.1.4.1.9.1.1"),
                BerBuilder.IpAddress(1, 2, 3, 4),
                BerBuilder.Integer(0),
                BerBuilder.Integer(0),
                BerBuilder.UnsignedApp(0x43, 1),
                BerBuilder.Sequence(0x30,
                    BerBuilder.Sequence(0x30, BerBuilder.Integer(1), BerBuilder.OctetString("bad")))));

        bool ok = SnmpBerReader.TryParse(frame, out SnmpTrapMessage? msg, out string? error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryParse_TooManyVarbinds_IsRejectedRatherThanAllocatingUnbounded()
    {
        byte[][] varbinds = Enumerable.Range(0, 10)
            .Select(i => BerBuilder.Sequence(0x30, BerBuilder.Oid($"1.3.6.1.2.1.2.2.1.1.{i + 1}"), BerBuilder.Integer(i)))
            .ToArray();

        byte[] frame = BerBuilder.Sequence(0x30,
            BerBuilder.Integer(0),
            BerBuilder.OctetString("public"),
            BerBuilder.Sequence(0xA4,
                BerBuilder.Oid("1.3.6.1.4.1.9.1.1"),
                BerBuilder.IpAddress(1, 2, 3, 4),
                BerBuilder.Integer(0),
                BerBuilder.Integer(0),
                BerBuilder.UnsignedApp(0x43, 1),
                BerBuilder.Sequence(0x30, varbinds)));

        bool ok = SnmpBerReader.TryParse(frame, out _, out string? error, maxVarbinds: 5);

        ok.Should().BeFalse();
        error.Should().Contain("varbinds");
    }

    [Fact]
    public void TryParse_ProtocolConfusion_V1PduTagWithV2cVersion_IsRejected()
    {
        byte[] frame = BerBuilder.Sequence(0x30,
            BerBuilder.Integer(1), // declares v2c
            BerBuilder.OctetString("public"),
            BerBuilder.Sequence(0xA4, BerBuilder.Null())); // but sends a v1 Trap-PDU tag

        bool ok = SnmpBerReader.TryParse(frame, out _, out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("PDU tag");
    }
}
