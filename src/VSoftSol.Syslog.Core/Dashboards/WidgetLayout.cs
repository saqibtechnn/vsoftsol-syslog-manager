namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// Where a widget sits on the responsive grid (PHASE_09 build item 4 — drag-to-arrange).
/// The grid is <see cref="Columns"/> columns wide; a widget occupies
/// <see cref="ColumnSpan"/>×<see cref="RowSpan"/> cells starting at
/// (<see cref="Column"/>, <see cref="Row"/>), both zero-based. On a narrow viewport the UI
/// collapses every widget to full width in <see cref="Row"/> then <see cref="Column"/>
/// order (UX_STANDARDS §8 — no horizontal scroll at 1366×768).
/// </summary>
public sealed record WidgetLayout
{
    /// <summary>The fixed grid width every dashboard lays out against.</summary>
    public const int Columns = 12;

    public required string WidgetId { get; init; }

    public int Column { get; init; }

    public int Row { get; init; }

    public int ColumnSpan { get; init; } = 4;

    public int RowSpan { get; init; } = 1;

    /// <summary>Clamps the placement to the grid and enforces a minimum span.</summary>
    public WidgetLayout Normalized()
    {
        int span = Math.Clamp(ColumnSpan, 2, Columns);
        int col = Math.Clamp(Column, 0, Columns - span);
        return this with
        {
            Column = col,
            ColumnSpan = span,
            Row = Math.Max(0, Row),
            RowSpan = Math.Clamp(RowSpan, 1, 6),
        };
    }
}
