namespace VSoftSol.Syslog.Core.Retention;

public enum ArchiveStatus
{
    Ok = 0,
    TamperDetected = 1,
    Missing = 2,
    Deleted = 3,
}

/// <summary>One exported, tamper-evidenced archive file (PHASE_10 build item 4).</summary>
public sealed record ArchiveRecord
{
    public long ArchiveId { get; init; }

    public long? StreamId { get; init; }

    public required string StreamName { get; init; }

    public required string FilePath { get; init; }

    public required DateTimeOffset PeriodStartUtc { get; init; }

    public required DateTimeOffset PeriodEndUtc { get; init; }

    public int EventCount { get; init; }

    public long ByteSize { get; init; }

    public required string Sha256 { get; init; }

    public ArchiveStatus Status { get; init; } = ArchiveStatus.Ok;

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? VerifiedUtc { get; init; }

    public DateTimeOffset? DeletedUtc { get; init; }
}
