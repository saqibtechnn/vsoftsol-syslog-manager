namespace VSoftSol.Syslog.Data.Secrets;

/// <summary>
/// Symmetric protect/unprotect for secret values at rest. The v1 implementation is DPAPI
/// (<see cref="DpapiSecretProtector"/>); the interface keeps <see cref="SqliteSecretStore"/>
/// unit-testable without touching the Windows keyring.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}
