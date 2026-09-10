namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// A named, owned dashboard (PHASE_09 build item 4). Holds its widgets and their grid
/// layout, a default time range and auto-refresh interval that widgets inherit, and the
/// sharing flags. Shipped default dashboards have <see cref="IsSystem"/> set: they cannot
/// be edited or deleted, only copied (build item 6). A shared dashboard's <em>definition</em>
/// is visible to everyone, but every widget query still runs under the viewer's own scope.
/// </summary>
public sealed record DashboardDefinition
{
    /// <summary>Row id. Zero for a dashboard that has not been persisted yet.</summary>
    public long Id { get; init; }

    public long OwnerUserId { get; init; }

    /// <summary>Shown in the list and the header. Rendered as text, never markup.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>When true, every authenticated user can open it (read-only). Only the owner edits.</summary>
    public bool IsShared { get; init; }

    /// <summary>A shipped default: non-deletable, non-editable, copyable.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Stable identity for a seeded dashboard (e.g. <c>network-overview</c>); null for user dashboards.</summary>
    public string? SystemKey { get; init; }

    /// <summary>The window widgets use unless they override it. Relative only.</summary>
    public TimeSpan DefaultTimeRange { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Auto-refresh cadence for the whole dashboard. <see cref="TimeSpan.Zero"/> ⇒ manual only.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.Zero;

    public IReadOnlyList<WidgetDefinition> Widgets { get; init; } = [];

    public IReadOnlyList<WidgetLayout> Layout { get; init; } = [];

    /// <summary>Set on read for a row the requesting user owns (drives the edit / delete affordances).</summary>
    public bool OwnedByCurrentUser { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>The layout entry for a widget, or a sensible default stacked position if absent.</summary>
    public WidgetLayout LayoutFor(WidgetDefinition widget)
    {
        foreach (WidgetLayout l in Layout)
        {
            if (l.WidgetId == widget.Id)
            {
                return l.Normalized();
            }
        }

        int index = 0;
        for (int i = 0; i < Widgets.Count; i++)
        {
            if (Widgets[i].Id == widget.Id)
            {
                index = i;
                break;
            }
        }

        return new WidgetLayout
        {
            WidgetId = widget.Id,
            Column = index % 3 * 4,
            Row = index / 3,
            ColumnSpan = 4,
            RowSpan = 1,
        };
    }
}
