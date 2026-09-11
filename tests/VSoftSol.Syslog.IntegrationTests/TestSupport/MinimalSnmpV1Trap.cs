using System.Text;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>A single hand-built, valid SNMPv1 trap datagram — test infrastructure only,
/// for exercising <c>SnmpTrapListener</c> end to end over a real UDP socket. The exhaustive
/// BER encode/decode matrix lives in the unit tests (<c>SnmpBerReaderTests</c>); this is
/// just enough wire bytes for one linkDown trap with a given community string.</summary>
internal static class MinimalSnmpV1Trap
{
    public static byte[] Build(string community)
    {
        byte[] varbind = Sequence(0x30, Oid("1.3.6.1.2.1.2.2.1.1.5"), OctetString("eth0"));
        byte[] pdu = Sequence(0xA4,
            Oid("1.3.6.1.4.1.9.1.1"),
            IpAddress(10, 0, 0, 1),
            Integer(2), // linkDown
            Integer(0),
            UnsignedApp(0x43, 12345),
            Sequence(0x30, varbind));

        return Sequence(0x30, Integer(0), OctetString(community), pdu);
    }

    private static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] len = content.Length < 0x80 ? [(byte)content.Length] : throw new NotSupportedException("test trap stays short-form");
        var result = new byte[1 + len.Length + content.Length];
        result[0] = tag;
        Array.Copy(len, 0, result, 1, len.Length);
        Array.Copy(content, 0, result, 1 + len.Length, content.Length);
        return result;
    }

    private static byte[] Sequence(byte tag, params byte[][] children)
    {
        int total = children.Sum(c => c.Length);
        var content = new byte[total];
        int offset = 0;
        foreach (byte[] c in children)
        {
            Array.Copy(c, 0, content, offset, c.Length);
            offset += c.Length;
        }

        return Tlv(tag, content);
    }

    private static byte[] Integer(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0xFF) };
        return Tlv(0x02, bytes.ToArray());
    }

    private static byte[] UnsignedApp(byte appTag, long value)
    {
        var bytes = new List<byte>();
        long v = value;
        do
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }
        while (v != 0);
        if ((bytes[0] & 0x80) != 0)
        {
            bytes.Insert(0, 0x00);
        }

        return Tlv(appTag, bytes.ToArray());
    }

    private static byte[] OctetString(string s) => Tlv(0x04, Encoding.Latin1.GetBytes(s));

    private static byte[] IpAddress(byte a, byte b, byte c, byte d) => Tlv(0x40, [a, b, c, d]);

    private static byte[] Oid(string dotted)
    {
        long[] arcs = dotted.Split('.').Select(long.Parse).ToArray();
        var content = new List<byte>();
        content.AddRange(EncodeBase128((arcs[0] * 40) + arcs[1]));
        for (int i = 2; i < arcs.Length; i++)
        {
            content.AddRange(EncodeBase128(arcs[i]));
        }

        return Tlv(0x06, content.ToArray());
    }

    private static List<byte> EncodeBase128(long value)
    {
        if (value == 0)
        {
            return [0];
        }

        var groups = new List<byte>();
        long v = value;
        while (v > 0)
        {
            groups.Insert(0, (byte)(v & 0x7F));
            v >>= 7;
        }

        for (int i = 0; i < groups.Count - 1; i++)
        {
            groups[i] |= 0x80;
        }

        return groups;
    }
}
