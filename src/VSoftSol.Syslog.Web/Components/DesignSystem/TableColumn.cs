using Microsoft.AspNetCore.Components;

namespace VSoftSol.Syslog.Web.Components.DesignSystem;

/// <summary>One column definition for <c>DataTable</c>.</summary>
public sealed class TableColumn<TItem>
{
    public required string Header { get; init; }

    /// <summary>Renders the cell for a row.</summary>
    public required RenderFragment<TItem> Cell { get; init; }

    /// <summary>Key used for sorting; when non-null the header is a sort control.</summary>
    public Func<TItem, IComparable?>? SortValue { get; init; }

    /// <summary>Stable id for the column chooser and sort state.</summary>
    public string Id => Header;

    /// <summary>Hidden by default (still offered in the column chooser).</summary>
    public bool HiddenByDefault { get; init; }

    /// <summary>Right-align (numbers, timestamps).</summary>
    public bool Numeric { get; init; }
}
