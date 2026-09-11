using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VSoftSol.Syslog.Core.Security.Mfa;

/// <summary>
/// RFC 6238 Time-based One-Time Password. HMAC-SHA1 is what the RFC mandates and what
/// every standard authenticator app (Google Authenticator, Authy, Microsoft Authenticator,
/// 1Password, ...) implements — this is an interoperability requirement of the algorithm,
/// not a weak-hash finding: TOTP's security rests on the shared secret's entropy and HMAC's
/// PRF property, which SHA-1's known collision attacks do not threaten. Recorded as an
/// accepted-by-design item in SECURITY_REVIEW.md.
/// </summary>
public static class TotpGenerator
{
    public const int DefaultDigits = 6;
    public static readonly TimeSpan DefaultStep = TimeSpan.FromSeconds(30);

    public static string GenerateCode(byte[] key, DateTimeOffset timestamp, TimeSpan? step = null, int digits = DefaultDigits) =>
        ComputeCode(key, StepCounter(timestamp, step ?? DefaultStep), digits);

    /// <summary>Accepts a code within <paramref name="window"/> steps of now in either
    /// direction, to tolerate clock drift between the server and the phone.</summary>
    public static bool ValidateCode(byte[] key, string code, DateTimeOffset timestamp, int window = 1, TimeSpan? step = null, int digits = DefaultDigits)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        TimeSpan s = step ?? DefaultStep;
        long counter = StepCounter(timestamp, s);
        for (long i = -window; i <= window; i++)
        {
            string candidate = ComputeCode(key, counter + i, digits);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(candidate), System.Text.Encoding.ASCII.GetBytes(code.Trim())))
            {
                return true;
            }
        }

        return false;
    }

    private static long StepCounter(DateTimeOffset timestamp, TimeSpan step) =>
        (long)(timestamp.ToUnixTimeSeconds() / step.TotalSeconds);

    private static string ComputeCode(byte[] key, long counter, int digits)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

#pragma warning disable CA5350 // RFC 6238 mandates HMAC-SHA1 for interoperability with every standard authenticator app; see the class remarks.
        byte[] hash = HMACSHA1.HashData(key, counterBytes);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        int truncated = binary % (int)Math.Pow(10, digits);
        return truncated.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}
