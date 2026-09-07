namespace VSoftSol.Syslog.Data.Audit;

/// <summary>A persisted audit row, as read back for the audit-log viewer (Auditor role).</summary>
public sealed record AuditRecord(
    long AuditId,
    DateTimeOffset OccurredUtc,
    string? Actor,
    string Action,
    string? EntityType,
    string? EntityId,
    string? SourceIp,
    string? BeforeJson,
    string? AfterJson,
    string? Detail);

/// <summary>Filter for <c>SqliteAuditLog.QueryAsync</c>.</summary>
public sealed record AuditQuery
{
    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public string? Actor { get; init; }

    public string? Action { get; init; }

    public int Limit { get; init; } = 200;

    public long Offset { get; init; }
}

/// <summary>Result of walking the audit hash chain.</summary>
/// <param name="Intact">True when every entry_hash follows from its predecessor.</param>
/// <param name="EntriesChecked">How many rows were verified.</param>
/// <param name="FirstBrokenAuditId">The first row whose hash does not verify, or null.</param>
public sealed record AuditChainVerification(bool Intact, long EntriesChecked, long? FirstBrokenAuditId);
