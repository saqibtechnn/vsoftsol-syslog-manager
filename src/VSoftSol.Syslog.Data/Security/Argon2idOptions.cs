using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Data.Security;

/// <summary>
/// Argon2id cost parameters (SECURITY_STANDARDS.md V2 — password storage). Bound from the
/// <c>Authentication:Argon2</c> configuration section. The defaults follow the OWASP
/// Password Storage Cheat Sheet's Argon2id recommendation (m = 19 MiB, t = 2, p = 1) and
/// can be raised by an operator as hardware improves.
/// </summary>
public sealed class Argon2idOptions
{
    public const string SectionName = "Authentication:Argon2";

    /// <summary>Memory cost in kibibytes. OWASP minimum 19456 (19 MiB).</summary>
    [Range(8_192, 1_048_576)]
    public int MemoryKib { get; set; } = 19_456;

    /// <summary>Time cost (iterations). OWASP minimum 2.</summary>
    [Range(1, 20)]
    public int Iterations { get; set; } = 2;

    /// <summary>Degree of parallelism (lanes).</summary>
    [Range(1, 16)]
    public int Parallelism { get; set; } = 1;

    /// <summary>Salt length in bytes.</summary>
    [Range(16, 64)]
    public int SaltBytes { get; set; } = 16;

    /// <summary>Derived-key length in bytes.</summary>
    [Range(16, 64)]
    public int HashBytes { get; set; } = 32;
}
