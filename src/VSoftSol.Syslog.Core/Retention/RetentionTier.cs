namespace VSoftSol.Syslog.Core.Retention;

/// <summary>
/// The tier a live <c>events</c> row is currently in (PHASE_10 build item 1). Cold and
/// Delete are not row states — a Cold event has already left the <c>events</c> table and
/// exists only inside an <see cref="Reports.ReportContent"/>-adjacent archive file; Delete
/// is the horizon at which the archive file itself is purged. See ADR 0018.
/// </summary>
public enum RetentionTier
{
    /// <summary>Uncompressed, fully indexed, searchable at full speed.</summary>
    Hot = 0,

    /// <summary>Message body and raw bytes are compressed at rest; still searchable via
    /// FTS (untouched), but rendering a result decompresses on read — slower, not blocked.</summary>
    Warm = 1,
}
