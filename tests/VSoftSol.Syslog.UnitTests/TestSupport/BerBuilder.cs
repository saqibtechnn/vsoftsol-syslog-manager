using System.Text;

namespace VSoftSol.Syslog.UnitTests.TestSupport;

/// <summary>
/// Minimal ASN.1 BER/TLV encoder — test infrastructure only. Builds valid (and
/// deliberately invalid) SNMP trap byte sequences for <c>SnmpBerReaderTests</c> without
/// hand-transcribing hex, which is error-prone for base-128 OID subidentifiers. There is no
/// production encoder anywhere in the product: the collector only ever receives traps.
/// </summary>
internal static class BerBuilder
{
    public static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] lenBytes = EncodeLength(content.Length);
        var result = new byte[1 + lenBytes.Length + content.Length];
        result[0] = tag;
        Array.Copy(lenBytes, 0, result, 1, lenBytes.Length);
        Array.Copy(content, 0, result, 1 + lenBytes.Length, content.Length);
        return result;
    }

    /// <summary>Wraps children's concatenated bytes under one tag — used for SEQUENCE
    /// (0x30) and, since SNMP PDUs are IMPLICIT-tagged sequences, for the PDU tags too.</summary>
    public static byte[] Sequence(byte tag, params byte[][] children)
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

    public static byte[] Integer(long value)
    {
        List<byte> bytes = TwosComplementMinimal(value);
        return Tlv(0x02, bytes.ToArray());
    }

    /// <summary>An APPLICATION-tagged unsigned value (TimeTicks 0x43, Counter32 0x41, ...).</summary>
    public static byte[] UnsignedApp(byte appTag, long value)
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

    public static byte[] OctetString(string s) => Tlv(0x04, Encoding.Latin1.GetBytes(s));

    public static byte[] OctetString(byte[] raw) => Tlv(0x04, raw);

    public static byte[] Null() => Tlv(0x05, []);

    public static byte[] IpAddress(byte a, byte b, byte c, byte d) => Tlv(0x40, [a, b, c, d]);

    public static byte[] Oid(string dotted)
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

    /// <summary>An OID whose encoded arc count exceeds any reasonable reader cap — every
    /// subidentifier after the first is the single value 1, so the arc count equals
    /// <paramref name="arcCount"/> exactly.</summary>
    public static byte[] OversizedOid(int arcCount)
    {
        var content = new List<byte> { 0x2B }; // first two arcs: 1.3
        for (int i = 2; i < arcCount; i++)
        {
            content.Add(0x01);
        }

        return Tlv(0x06, content.ToArray());
    }

    private static List<byte> TwosComplementMinimal(long value)
    {
        var bytes = new List<byte>();
        long v = value;
        do
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }
        while (v != 0 && v != -1);

        bool negative = value < 0;
        if (negative && (bytes[0] & 0x80) == 0)
        {
            bytes.Insert(0, 0xFF);
        }
        else if (!negative && (bytes[0] & 0x80) != 0)
        {
            bytes.Insert(0, 0x00);
        }

        return bytes;
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

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return [(byte)length];
        }

        var bytes = new List<byte>();
        int v = length;
        while (v > 0)
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }

        var result = new byte[1 + bytes.Count];
        result[0] = (byte)(0x80 | bytes.Count);
        bytes.CopyTo(result, 1);
        return result;
    }
}
