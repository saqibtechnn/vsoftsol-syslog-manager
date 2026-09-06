using System.Text;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// Turns the verbatim payload bytes into text for the parsers (PHASE_03 item 5): BOM
/// detection, strict UTF-8 with a latin-1 fallback for legacy devices, and a character
/// cap whose overflow is recorded as the <c>truncated</c> field, never silently cut.
/// The caller always keeps the original bytes in <c>raw_message</c> (Constraint 4).
/// </summary>
public static class PayloadDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly char[] TrimChars = ['\r', '\n', ' ', '﻿'];

    public readonly record struct Decoded(string Text, string Charset, bool Truncated);

    public static Decoded Decode(ReadOnlySpan<byte> payload, int maxChars)
    {
        (string text, string charset) = DecodeBytes(payload);

        // Normalise leading/trailing CR/LF/space/BOM (the TCP framer strips \r\n but a lone
        // one can still arrive over UDP). AsSpan().Trim(ReadOnlySpan) does not allocate a
        // params array; only substring when something is actually trimmed.
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim(TrimChars);
        if (trimmed.Length != text.Length)
        {
            text = trimmed.ToString();
        }

        bool truncated = false;
        if (maxChars > 0 && text.Length > maxChars)
        {
            text = text[..maxChars];
            truncated = true;
        }

        return new Decoded(text, charset, truncated);
    }

    private static (string Text, string Charset) DecodeBytes(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
        {
            return (DecodeUtf8OrLatin1(payload[3..]).Text, "utf-8");
        }

        if (payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(payload[2..]), "utf-16le");
        }

        if (payload.Length >= 2 && payload[0] == 0xFE && payload[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(payload[2..]), "utf-16be");
        }

        return DecodeUtf8OrLatin1(payload);
    }

    private static (string Text, string Charset) DecodeUtf8OrLatin1(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return (StrictUtf8.GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Latin1.GetString(bytes), "latin-1");
        }
    }
}
