using System.Security.Cryptography;
using System.Text;

namespace VSoftSol.Syslog.Core.Security.Mfa;

/// <summary>
/// One-time MFA recovery codes (PHASE_11 item 8). Codes are high-entropy random values, not
/// user-chosen secrets, so a fast SHA-256 hash (not Argon2id — that cost is for defending a
/// low-entropy human password against offline guessing, which does not apply here) is
/// sufficient and cheap to verify on every login attempt.
/// </summary>
public static class RecoveryCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I — avoids transcription mistakes
    private const int GroupLength = 4;
    private const int GroupCount = 3;

    public static IReadOnlyList<string> Generate(int count = 10)
    {
        var codes = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            codes.Add(GenerateOne());
        }

        return codes;
    }

    private static string GenerateOne()
    {
        var sb = new StringBuilder((GroupLength * GroupCount) + (GroupCount - 1));
        for (int g = 0; g < GroupCount; g++)
        {
            if (g > 0)
            {
                sb.Append('-');
            }

            for (int c = 0; c < GroupLength; c++)
            {
                int index = RandomNumberGenerator.GetInt32(Alphabet.Length);
                sb.Append(Alphabet[index]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Hex SHA-256 of the code's normalised form (upper-case, hyphens intact) —
    /// stored at rest instead of the plaintext code.</summary>
    public static string Hash(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)));
        return Convert.ToHexString(hash);
    }

    public static bool Verify(string code, string hash)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        string computed = Hash(code);
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(computed), Convert.FromHexString(hash));
    }

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();
}
