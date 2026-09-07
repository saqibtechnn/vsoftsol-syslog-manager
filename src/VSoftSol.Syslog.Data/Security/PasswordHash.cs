namespace VSoftSol.Syslog.Data.Security;

/// <summary>
/// Produces and verifies Argon2id password hashes in PHC string format
/// (<c>$argon2id$v=19$m=...,t=...,p=...$salt$hash</c>). The parameters travel inside the
/// string, so a stored hash stays verifiable after an operator raises the cost settings
/// and old hashes are transparently upgraded on the next successful login.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hash <paramref name="password"/> with the current cost parameters. Never returns null/empty.</summary>
    string Hash(string password);

    /// <summary>
    /// Constant-time verification of <paramref name="password"/> against <paramref name="phcString"/>.
    /// <paramref name="needsRehash"/> is set when the stored parameters are weaker than the
    /// current configuration. Returns false (never throws) for a malformed stored hash.
    /// </summary>
    bool Verify(string password, string phcString, out bool needsRehash);
}
