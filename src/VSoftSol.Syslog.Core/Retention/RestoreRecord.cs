namespace VSoftSol.Syslog.Core.Retention;

public enum RestoreStatus
{
    Active = 0,
    Expired = 1,
}

/// <summary>A temporary reinstatement of one archive's events into the live, searchable
/// database (PHASE_10 build item 5) — always audited, always auto-expiring.</summary>
public sealed record RestoreRecord
{
    public long RestoreId { get; init; }

    public long ArchiveId { get; init; }

    public required string RequestedBy { get; init; }

    public DateTimeOffset RequestedUtc { get; init; }

    public DateTimeOffset ExpiresUtc { get; init; }

    public int EventCount { get; init; }

    public RestoreStatus Status { get; init; } = RestoreStatus.Active;

    public DateTimeOffset? ExpiredUtc { get; init; }
}
