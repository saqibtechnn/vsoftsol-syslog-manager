namespace VSoftSol.Syslog.Data.Search;

/// <summary>A named, owned search. Stores the query, never the results (PHASE_05 item 6).</summary>
public sealed record SavedSearch
{
    public required long Id { get; init; }

    public required long OwnerUserId { get; init; }

    public required string Name { get; init; }

    public string QueryText { get; init; } = string.Empty;

    /// <summary>Serialised time range the search was saved with, or null for "use the current range".</summary>
    public string? TimeRangeJson { get; init; }

    /// <summary>When true, every authenticated user can load it (read-only). Only the owner can edit or delete.</summary>
    public bool IsShared { get; init; }

    /// <summary>True on rows returned to a user who is not the owner (a shared search).</summary>
    public bool OwnedByCurrentUser { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}
