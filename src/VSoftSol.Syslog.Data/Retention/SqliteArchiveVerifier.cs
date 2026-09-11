using VSoftSol.Syslog.Core.Retention;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>One archive's verification outcome.</summary>
public sealed record ArchiveVerification(long ArchiveId, string StreamName, ArchiveStatus Status, string? Detail);

/// <summary>
/// The scheduled tamper-detection job (PHASE_10 build item 4 / SECURITY_STANDARDS.md
/// "Archive tampering"). Re-hashes each due archive and compares to the value recorded at
/// creation — a value an attacker with only filesystem access cannot update in step,
/// because it lives in the database, not beside the file (documented honestly as a
/// residual limitation: an attacker with <em>both</em> filesystem and database write
/// access could update both — no software control defends against that; only file-system
/// and backup-integrity controls at the OS layer can).
/// </summary>
public sealed class SqliteArchiveVerifier
{
    private readonly SqliteArchiveStore _archives;
    private readonly SqliteRetentionPolicyStore _policies;

    public SqliteArchiveVerifier(SqliteArchiveStore archives, SqliteRetentionPolicyStore policies)
    {
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    public async Task<IReadOnlyList<ArchiveVerification>> VerifyDueBatchAsync(
        TimeSpan staleAfter, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveRecord> due = await _archives.ListDueForVerificationAsync(staleAfter, limit, cancellationToken).ConfigureAwait(false);
        RetentionSettings settings = await _policies.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        string root = string.IsNullOrWhiteSpace(settings.ArchiveRoot)
            ? Path.Combine(Path.GetDirectoryName(due.Count > 0 ? due[0].FilePath : ".") ?? ".", "..")
            : settings.ArchiveRoot;

        var results = new List<ArchiveVerification>(due.Count);
        foreach (ArchiveRecord archive in due)
        {
            results.Add(await VerifyOneAsync(archive, root, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<ArchiveVerification> VerifyOneAsync(ArchiveRecord archive, string root, CancellationToken cancellationToken)
    {
        if (!ArchiveFileStore.Exists(archive.FilePath))
        {
            await _archives.MarkMissingAsync(archive.ArchiveId, cancellationToken).ConfigureAwait(false);
            return new ArchiveVerification(archive.ArchiveId, archive.StreamName, ArchiveStatus.Missing, "the archive file no longer exists on disk");
        }

        byte[] content;
        try
        {
            content = await ArchiveFileStore.ReadAllBytesAsync(Path.GetFullPath(root), archive.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The recorded path no longer resolves under the current archive root (e.g. the
            // root was reconfigured) — read it directly; this is a verification read, not a
            // write, and the path itself came from our own database, not user input.
            content = await File.ReadAllBytesAsync(archive.FilePath, cancellationToken).ConfigureAwait(false);
        }

        string actualHash = ArchiveFileStore.ComputeSha256Hex(content);
        if (!string.Equals(actualHash, archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await _archives.MarkTamperedAsync(archive.ArchiveId, cancellationToken).ConfigureAwait(false);
            return new ArchiveVerification(
                archive.ArchiveId, archive.StreamName, ArchiveStatus.TamperDetected,
                $"expected sha256 {archive.Sha256}, computed {actualHash}");
        }

        await _archives.MarkVerifiedAsync(archive.ArchiveId, cancellationToken).ConfigureAwait(false);
        return new ArchiveVerification(archive.ArchiveId, archive.StreamName, ArchiveStatus.Ok, null);
    }
}
