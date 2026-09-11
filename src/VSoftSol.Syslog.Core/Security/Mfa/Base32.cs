namespace VSoftSol.Syslog.Core.Security.Mfa;

/// <summary>
/// RFC 4648 base32 (the alphabet every TOTP authenticator app expects for a manually-typed
/// secret). Decoding tolerates missing padding and lower-case input — the two ways a human
/// typing a secret by hand most often deviates from the strict spec.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder((data.Length * 8 / 5) + 1);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                sb.Append(Alphabet[index]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            sb.Append(Alphabet[index]);
        }

        return sb.ToString();
    }

    public static byte[] Decode(string base32)
    {
        ArgumentNullException.ThrowIfNull(base32);
        string cleaned = base32.Trim().TrimEnd('=').ToUpperInvariant();
        if (cleaned.Length == 0)
        {
            return [];
        }

        var bytes = new List<byte>((cleaned.Length * 5 / 8) + 1);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (char c in cleaned)
        {
            int index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                throw new FormatException($"'{c}' is not a valid base32 character.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }
}
