namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// A saved results-grid column layout for one user (PHASE_05 item 3: "saved column layouts
/// per user"). <see cref="LayoutJson"/> is opaque to the data layer — the grid component
/// owns its shape.
/// </summary>
public sealed record ColumnLayout
{
    public required long Id { get; init; }

    public required long UserId { get; init; }

    public required string Name { get; init; }

    public required string LayoutJson { get; init; }

    public bool IsDefault { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}
