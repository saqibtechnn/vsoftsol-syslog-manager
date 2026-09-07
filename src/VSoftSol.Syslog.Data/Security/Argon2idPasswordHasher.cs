using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Data.Security;

/// <summary>
/// Argon2id implementation of <see cref="IPasswordHasher"/> using PHC string format.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const string Prefix = "$argon2id$v=19$";

    private readonly Argon2idOptions _options;

    public Argon2idPasswordHasher(IOptions<Argon2idOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public Argon2idPasswordHasher(Argon2idOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(_options.SaltBytes);
        byte[] hash = Derive(password, salt, _options.MemoryKib, _options.Iterations, _options.Parallelism, _options.HashBytes);

        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}m={_options.MemoryKib},t={_options.Iterations},p={_options.Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool Verify(string password, string phcString, out bool needsRehash)
    {
        needsRehash = false;
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrEmpty(phcString) || !TryParse(phcString, out Parsed p))
        {
            // Spend a comparable amount of work so a malformed/absent hash is not a timing oracle.
            _ = Derive(password, new byte[_options.SaltBytes], _options.MemoryKib, _options.Iterations, _options.Parallelism, _options.HashBytes);
            return false;
        }

        byte[] candidate = Derive(password, p.Salt, p.MemoryKib, p.Iterations, p.Parallelism, p.Hash.Length);
        bool ok = CryptographicOperations.FixedTimeEquals(candidate, p.Hash);

        if (ok)
        {
            needsRehash = p.MemoryKib < _options.MemoryKib
                || p.Iterations < _options.Iterations
                || p.Parallelism != _options.Parallelism
                || p.Hash.Length < _options.HashBytes
                || p.Salt.Length < _options.SaltBytes;
        }

        return ok;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int hashBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(hashBytes);
    }

    private static bool TryParse(string phc, out Parsed parsed)
    {
        parsed = default;
        if (!phc.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // $argon2id$v=19$m=..,t=..,p=..$<salt b64>$<hash b64>
        string[] parts = phc.Split('$', StringSplitOptions.None);
        if (parts.Length != 6)
        {
            return false;
        }

        int memoryKib = 0, iterations = 0, parallelism = 0;
        foreach (string kv in parts[3].Split(','))
        {
            int eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                return false;
            }

            string key = kv[..eq];
            if (!int.TryParse(kv[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
            {
                return false;
            }

            switch (key)
            {
                case "m": memoryKib = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        if (memoryKib == 0 || iterations == 0 || parallelism == 0)
        {
            return false;
        }

        try
        {
            parsed = new Parsed(memoryKib, iterations, parallelism, Convert.FromBase64String(parts[4]), Convert.FromBase64String(parts[5]));
        }
        catch (FormatException)
        {
            return false;
        }

        return parsed.Salt.Length >= 8 && parsed.Hash.Length >= 16;
    }

    private readonly record struct Parsed(int MemoryKib, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}
