using System.Security.Cryptography;
using VSoftSol.Syslog.Core.Retention;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>
/// Filesystem operations for archive files — atomic write (temp file + rename, so a crash
/// never leaves a half-written archive at its real name), SHA-256 over the complete file
/// content, and root-containment re-verification immediately before every open
/// (SECURITY_STANDARDS.md "Path traversal ... archive naming"; PHASE_10 build item 4).
/// </summary>
internal static class ArchiveFileStore
{
    public static async Task<(string Sha256Hex, long ByteSize)> WriteAtomicAsync(
        string rootFullPath, string fullPath, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(content);

        if (!ArchiveNaming.IsSafeUnderRoot(rootFullPath, fullPath))
        {
            throw new InvalidOperationException($"Refusing to write outside the archive root: '{fullPath}'.");
        }

        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"'{fullPath}' has no directory component.");
        Directory.CreateDirectory(directory);

        string tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, fullPath, overwrite: true);

        return (ComputeSha256Hex(content), content.LongLength);
    }

    public static async Task<byte[]> ReadAllBytesAsync(string rootFullPath, string fullPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (!ArchiveNaming.IsSafeUnderRoot(rootFullPath, fullPath))
        {
            throw new InvalidOperationException($"Refusing to read outside the archive root: '{fullPath}'.");
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    public static bool Exists(string fullPath) => File.Exists(fullPath);

    public static void Delete(string fullPath)
    {
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    public static string ComputeSha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
