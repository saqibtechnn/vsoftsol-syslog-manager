using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Components.DesignSystem;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.DesignSystem;

/// <summary>
/// The shared table used the framework's <c>Virtualize</c>, which positions its scroll spacers with
/// inline <c>style</c> attributes. The page CSP blocks those, so on a long list (the audit log,
/// devices) the spacers collapsed and the list rendered wrongly. Long tables now render in windows
/// with an explicit "Show more" control, and emit no inline styles.
/// </summary>
public sealed class DataTableWindowingTests
{
    private static readonly Regex InlineStyle = new(@"\sstyle\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Task<string> RenderAsync(int rows, bool virtualized = true)
    {
        IReadOnlyList<int> items = [.. Enumerable.Range(1, rows)];
        IReadOnlyList<TableColumn<int>> columns =
        [
            new TableColumn<int>
            {
                Header = "Number",
                Cell = n => (RenderTreeBuilder b) => b.AddContent(0, n),
            },
        ];
        return RazorRenderer.RenderAsync<DataTable<int>>(new()
        {
            ["Items"] = items,
            ["Columns"] = columns,
            ["Virtualized"] = virtualized,
        });
    }

    private static int BodyRows(string html) =>
        Regex.Matches(Regex.Match(html, @"<tbody>.*?</tbody>", RegexOptions.Singleline).Value, @"<tr[\s>]").Count;

    [Fact]
    public async Task LongTable_RendersFirstWindowOnly_WithAShowMoreControlAndNoInlineStyle()
    {
        string html = await RenderAsync(250);

        InlineStyle.IsMatch(html).Should().BeFalse("inline styles are blocked by the page CSP");
        BodyRows(html).Should().Be(101, "the first 100 rows plus the show-more row");
        html.Should().Contain("Show 100 more").And.Contain("150 remaining");
    }

    [Fact]
    public async Task ShortTable_RendersEveryRow_WithNoShowMoreControl()
    {
        string html = await RenderAsync(60);

        BodyRows(html).Should().Be(60);
        html.Should().NotContain("Show 100 more");
        InlineStyle.IsMatch(html).Should().BeFalse();
    }

    [Fact]
    public async Task NonVirtualizedTable_RendersEveryRow()
    {
        string html = await RenderAsync(250, virtualized: false);

        BodyRows(html).Should().Be(250);
        html.Should().NotContain("Show 100 more");
    }
}
