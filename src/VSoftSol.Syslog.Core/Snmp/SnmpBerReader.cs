using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace VSoftSol.Syslog.Core.Snmp;

/// <summary>
/// A minimal, bounded ASN.1 BER/TLV reader for SNMPv1/v2c trap PDUs (PHASE_11 item 2). This
/// is not a general-purpose BER library or a MIB compiler — it decodes exactly the tags an
/// SNMP trap uses and nothing else, and every step is bounds-checked so a malformed or
/// hostile datagram returns <c>false</c> instead of throwing, hanging, or over-allocating
/// (SECURITY_STANDARDS.md: "malformed varbinds, oversized OIDs ... rejected without
/// crashing"). Callers get varbinds back as MIB-free name/value pairs.
/// </summary>
public static class SnmpBerReader
{
    private const byte TagInteger = 0x02;
    private const byte TagOctetString = 0x04;
    private const byte TagNull = 0x05;
    private const byte TagOid = 0x06;
    private const byte TagSequence = 0x30;
    private const byte TagIpAddress = 0x40; // [APPLICATION 0] IMPLICIT OCTET STRING
    private const byte TagCounter32 = 0x41; // [APPLICATION 1]
    private const byte TagGauge32 = 0x42; // [APPLICATION 2]
    private const byte TagTimeTicks = 0x43; // [APPLICATION 3]
    private const byte TagOpaque = 0x44; // [APPLICATION 4]
    private const byte TagCounter64 = 0x46; // [APPLICATION 6]
    private const byte TagTrapPduV1 = 0xA4; // [4] Trap-PDU (RFC 1157)
    private const byte TagTrapPduV2 = 0xA7; // [7] SNMPv2-Trap-PDU (RFC 3416)

    private const string SysUpTimeOid = "1.3.6.1.2.1.1.3.0";
    private const string SnmpTrapOidOid = "1.3.6.1.6.3.1.1.4.1.0";

    public const int DefaultMaxVarbinds = 256;
    public const int DefaultMaxOidArcs = 128;

    /// <summary>Attempts to decode one SNMPv1 or SNMPv2c trap message. Never throws for any
    /// input — a malformed, truncated, or oversized datagram sets <paramref name="error"/>
    /// and returns <c>false</c>.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        [NotNullWhen(true)] out SnmpTrapMessage? message,
        out string? error,
        int maxVarbinds = DefaultMaxVarbinds,
        int maxOidArcs = DefaultMaxOidArcs)
    {
        message = null;
        error = null;
        byte[] bytes = data.ToArray(); // SNMP datagrams are small; owning the buffer keeps every helper a plain array indexer

        try
        {
            int pos = 0;
            (byte outerTag, int outerStart, int outerLen) = ReadTlv(bytes, ref pos, bytes.Length);
            if (outerTag != TagSequence)
            {
                error = $"expected SEQUENCE (0x30) for the message envelope, got 0x{outerTag:X2}.";
                return false;
            }

            int end = outerStart + outerLen;
            int p = outerStart;

            (byte tVer, int sVer, int lVer) = ReadTlv(bytes, ref p, end);
            if (tVer != TagInteger || lVer != 1)
            {
                error = "bad SNMP version field.";
                return false;
            }

            int versionRaw = bytes[sVer];
            if (versionRaw != (int)SnmpVersion.V1 && versionRaw != (int)SnmpVersion.V2c)
            {
                error = $"unsupported SNMP version {versionRaw}.";
                return false;
            }

            var version = (SnmpVersion)versionRaw;

            (byte tCom, int sCom, int lCom) = ReadTlv(bytes, ref p, end);
            if (tCom != TagOctetString)
            {
                error = "bad SNMP community field.";
                return false;
            }

            string community = Encoding.Latin1.GetString(bytes, sCom, lCom);

            (byte pduTag, int pduStart, int pduLen) = ReadTlv(bytes, ref p, end);
            int pduEnd = pduStart + pduLen;
            int q = pduStart;

            string? agentAddress = null;
            string? enterpriseOid = null;
            string? trapOid = null;
            int? genericTrap = null;
            int? specificTrap = null;
            long? uptime = null;
            var varbinds = new List<SnmpVarbind>();

            if (pduTag == TagTrapPduV1 && version == SnmpVersion.V1)
            {
                (byte tEnt, int sEnt, int lEnt) = ReadTlv(bytes, ref q, pduEnd);
                if (tEnt != TagOid || !TryDecodeOid(bytes, sEnt, lEnt, maxOidArcs, out enterpriseOid))
                {
                    error = "bad or oversized enterprise OID.";
                    return false;
                }

                (byte tAgent, int sAgent, int lAgent) = ReadTlv(bytes, ref q, pduEnd);
                if (tAgent != TagIpAddress || lAgent != 4)
                {
                    error = "bad agent-addr.";
                    return false;
                }

                agentAddress = FormatIpV4(bytes, sAgent);

                (byte tGen, int sGen, int lGen) = ReadTlv(bytes, ref q, pduEnd);
                if (tGen != TagInteger)
                {
                    error = "bad generic-trap.";
                    return false;
                }

                genericTrap = (int)DecodeSignedInteger(bytes, sGen, lGen);

                (byte tSpec, int sSpec, int lSpec) = ReadTlv(bytes, ref q, pduEnd);
                if (tSpec != TagInteger)
                {
                    error = "bad specific-trap.";
                    return false;
                }

                specificTrap = (int)DecodeSignedInteger(bytes, sSpec, lSpec);

                (byte tTime, int sTime, int lTime) = ReadTlv(bytes, ref q, pduEnd);
                if (tTime != TagTimeTicks)
                {
                    error = "bad time-stamp.";
                    return false;
                }

                uptime = DecodeUnsignedInteger(bytes, sTime, lTime);

                if (!TryReadVarbindList(bytes, ref q, pduEnd, maxVarbinds, maxOidArcs, varbinds, out error))
                {
                    return false;
                }
            }
            else if (pduTag == TagTrapPduV2 && version == SnmpVersion.V2c)
            {
                (byte tReq, int sReq, int lReq) = ReadTlv(bytes, ref q, pduEnd);
                if (tReq != TagInteger)
                {
                    error = "bad request-id.";
                    return false;
                }

                (byte tEs, int sEs, int lEs) = ReadTlv(bytes, ref q, pduEnd);
                if (tEs != TagInteger)
                {
                    error = "bad error-status.";
                    return false;
                }

                (byte tEi, int sEi, int lEi) = ReadTlv(bytes, ref q, pduEnd);
                if (tEi != TagInteger)
                {
                    error = "bad error-index.";
                    return false;
                }

                if (!TryReadVarbindList(bytes, ref q, pduEnd, maxVarbinds, maxOidArcs, varbinds, out error))
                {
                    return false;
                }

                foreach (SnmpVarbind vb in varbinds)
                {
                    if (vb.Oid == SysUpTimeOid && long.TryParse(vb.DisplayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long u))
                    {
                        uptime = u;
                    }
                    else if (vb.Oid == SnmpTrapOidOid)
                    {
                        trapOid = vb.DisplayValue;
                    }
                }
            }
            else
            {
                error = $"PDU tag 0x{pduTag:X2} does not match the declared SNMP version.";
                return false;
            }

            message = new SnmpTrapMessage(version, community, agentAddress, enterpriseOid, genericTrap, specificTrap, uptime, trapOid, varbinds);
            return true;
        }
        catch (BerFormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            error = "truncated BER data.";
            return false;
        }
    }

    private static bool TryReadVarbindList(
        byte[] bytes, ref int pos, int end, int maxVarbinds, int maxOidArcs, List<SnmpVarbind> into, out string? error)
    {
        error = null;
        (byte tList, int sList, int lList) = ReadTlv(bytes, ref pos, end);
        if (tList != TagSequence)
        {
            error = "bad variable-bindings list.";
            return false;
        }

        int listEnd = sList + lList;
        int p = sList;
        while (p < listEnd)
        {
            if (into.Count >= maxVarbinds)
            {
                error = $"more than {maxVarbinds} varbinds — refusing rather than allocating unbounded.";
                return false;
            }

            (byte tVb, int sVb, int lVb) = ReadTlv(bytes, ref p, listEnd);
            if (tVb != TagSequence)
            {
                error = "bad VarBind.";
                return false;
            }

            int vbEnd = sVb + lVb;
            int vp = sVb;
            (byte tName, int sName, int lName) = ReadTlv(bytes, ref vp, vbEnd);
            if (tName != TagOid || !TryDecodeOid(bytes, sName, lName, maxOidArcs, out string? oid))
            {
                error = "bad or oversized varbind OID.";
                return false;
            }

            (byte tVal, int sVal, int lVal) = ReadTlv(bytes, ref vp, vbEnd);
            (SnmpVarbindKind kind, string display) = DecodeValue(tVal, bytes, sVal, lVal, maxOidArcs);
            into.Add(new SnmpVarbind(oid!, kind, display));
        }

        return true;
    }

    private static (SnmpVarbindKind Kind, string Display) DecodeValue(byte tag, byte[] bytes, int start, int len, int maxOidArcs) => tag switch
    {
        TagInteger => (SnmpVarbindKind.Integer, DecodeSignedInteger(bytes, start, len).ToString(CultureInfo.InvariantCulture)),
        TagOctetString => (SnmpVarbindKind.OctetString, DecodeDisplayString(bytes, start, len)),
        TagNull => (SnmpVarbindKind.Null, string.Empty),
        TagOid => (SnmpVarbindKind.ObjectIdentifier, TryDecodeOid(bytes, start, len, maxOidArcs, out string? oid) ? oid! : "(oversized)"),
        TagIpAddress when len == 4 => (SnmpVarbindKind.IpAddress, FormatIpV4(bytes, start)),
        TagCounter32 => (SnmpVarbindKind.Counter32, DecodeUnsignedInteger(bytes, start, len).ToString(CultureInfo.InvariantCulture)),
        TagGauge32 => (SnmpVarbindKind.Gauge32, DecodeUnsignedInteger(bytes, start, len).ToString(CultureInfo.InvariantCulture)),
        TagTimeTicks => (SnmpVarbindKind.TimeTicks, DecodeUnsignedInteger(bytes, start, len).ToString(CultureInfo.InvariantCulture)),
        TagCounter64 => (SnmpVarbindKind.Counter64, DecodeUnsignedInteger(bytes, start, len).ToString(CultureInfo.InvariantCulture)),
        TagOpaque => (SnmpVarbindKind.Other, DecodeDisplayString(bytes, start, len)),
        _ => (SnmpVarbindKind.Other, $"0x{tag:X2}:{Convert.ToHexString(bytes, start, Math.Min(len, 64))}"),
    };

    /// <summary>Renders an octet-string varbind as text when printable, else as hex — a
    /// vendor-specific binary varbind must never throw or produce mojibake.</summary>
    private static string DecodeDisplayString(byte[] bytes, int start, int len)
    {
        for (int i = start; i < start + len; i++)
        {
            byte b = bytes[i];
            bool controlChar = b < 0x09 || (b > 0x0D && b < 0x20) || b == 0x7F;
            if (controlChar)
            {
                return "0x" + Convert.ToHexString(bytes, start, Math.Min(len, 256));
            }
        }

        return Encoding.Latin1.GetString(bytes, start, len);
    }

    private static string FormatIpV4(byte[] bytes, int start) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes[start]}.{bytes[start + 1]}.{bytes[start + 2]}.{bytes[start + 3]}");

    private static long DecodeSignedInteger(byte[] bytes, int start, int len)
    {
        if (len is < 1 or > 8)
        {
            throw new BerFormatException("INTEGER length out of range.");
        }

        long value = (bytes[start] & 0x80) != 0 ? -1 : 0; // sign-extend from the leading bit
        for (int i = 0; i < len; i++)
        {
            value = (value << 8) | bytes[start + i];
        }

        return value;
    }

    private static long DecodeUnsignedInteger(byte[] bytes, int start, int len)
    {
        if (len is < 1 or > 9) // Counter64 can carry a 9th byte of leading zero to stay positive
        {
            throw new BerFormatException("counter/gauge/timeticks length out of range.");
        }

        long value = 0;
        for (int i = 0; i < len; i++)
        {
            value = (value << 8) | bytes[start + i];
        }

        return value;
    }

    /// <summary>Decodes an OBJECT IDENTIFIER's base-128 subidentifiers into dotted-decimal
    /// text. Returns <c>false</c> — never throws — once the arc count exceeds
    /// <paramref name="maxArcs"/>, which is the "oversized OID" defense.</summary>
    private static bool TryDecodeOid(byte[] bytes, int start, int len, int maxArcs, out string? oid)
    {
        oid = null;
        if (len < 1)
        {
            return false;
        }

        var arcs = new List<long>(Math.Min(maxArcs, 32));
        int first = bytes[start];
        arcs.Add(first / 40);
        arcs.Add(first % 40);

        long current = 0;
        bool midArc = false;
        for (int i = start + 1; i < start + len; i++)
        {
            if (arcs.Count > maxArcs)
            {
                return false;
            }

            byte b = bytes[i];
            current = (current << 7) | (uint)(b & 0x7F);
            midArc = true;
            if (current > int.MaxValue)
            {
                return false; // a single pathological subidentifier growing unbounded
            }

            if ((b & 0x80) == 0)
            {
                arcs.Add(current);
                current = 0;
                midArc = false;
            }
        }

        if (midArc || arcs.Count > maxArcs)
        {
            return false; // truncated final subidentifier, or the arc cap was hit exactly at the end
        }

        oid = string.Join('.', arcs);
        return true;
    }

    private static (byte Tag, int ContentStart, int ContentLength) ReadTlv(byte[] bytes, ref int pos, int end)
    {
        if (pos >= end)
        {
            throw new BerFormatException("truncated BER: expected a tag.");
        }

        byte tag = bytes[pos++];
        if (pos >= end)
        {
            throw new BerFormatException("truncated BER: expected a length.");
        }

        byte lenByte = bytes[pos++];
        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            int numLenBytes = lenByte & 0x7F;
            if (numLenBytes == 0)
            {
                throw new BerFormatException("indefinite-length BER encoding is not supported.");
            }

            if (numLenBytes > 4 || pos + numLenBytes > end)
            {
                throw new BerFormatException("BER length field is malformed or runs past the buffer.");
            }

            length = 0;
            for (int i = 0; i < numLenBytes; i++)
            {
                length = (length << 8) | bytes[pos++];
            }
        }

        if (length < 0 || pos + length > end)
        {
            throw new BerFormatException("BER length exceeds the remaining buffer.");
        }

        int contentStart = pos;
        pos += length;
        return (tag, contentStart, length);
    }

    private sealed class BerFormatException(string message) : Exception(message);
}
