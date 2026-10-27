using System.Text.RegularExpressions;
using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Components.Pages.Dashboards.Widgets;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// The UI sends <c>style-src 'self' 'nonce-…'</c> (no <c>'unsafe-inline'</c>; SECURITY_STANDARDS), so a
/// browser silently drops every inline <c>style="…"</c> attribute. The dashboard grid and bar charts
/// used them, so in a real browser every widget collapsed into a one-column-wide strip while the
/// server-rendered HTML — which these tests read — looked fine. Placement and sizing therefore
/// must come from classes, and the stylesheet must define every class the markup can emit.
/// </summary>
public sealed class CspInlineStyleTests : IClassFixture<SyslogWebApplicationFactory>
{
    private static readonly Regex InlineStyleAttribute = new(@"\sstyle\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SyslogWebApplicationFactory _factory;

    public CspInlineStyleTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private async Task<WebAuthClient> SignedInAsync(string user)
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync(user, "csp-Test-Password-Long-1", Role.Administrator);
        return auth;
    }

    [Fact]
    public async Task BarChart_WithData_EmitsNoInlineStyleAttribute_AndAWidthClass()
    {
        var result = new AggregationResult
        {
            Points = [new AggPoint("web-1", null, 120), new AggPoint("web-2", null, 60), new AggPoint("db-1", null, 18)],
            Plan = null,
        };

        string html = await RazorRenderer.RenderAsync<BarChart>(new() { ["Result"] = result, ["TopN"] = 10 });

        InlineStyleAttribute.IsMatch(html).Should().BeFalse("inline styles are blocked by the page's CSP");
        html.Should().Contain("ds-pct-100", "the largest bar fills its track")
            .And.Contain("ds-pct-50", "a bar half the maximum is half full");
    }

    [Fact]
    public async Task EveryShippedDashboard_HasNoInlineStyleAttribute_AndPlacesCellsWithClasses()
    {
        WebAuthClient auth = await SignedInAsync("csp-dash");
        string list = await auth.Client.GetStringAsync("/dashboards");
        string[] paths = Regex.Matches(list, @"href=""(/dashboards/\d+)""").Select(m => m.Groups[1].Value).Distinct().ToArray();
        paths.Should().HaveCountGreaterThanOrEqualTo(4, "the four shipped dashboards are listed");

        foreach (string path in paths)
        {
            string html = await auth.Client.GetStringAsync(path);

            html.Should().Contain("ds-dashgrid__cell", $"{path} renders its widget grid");
            Offender(html).Should().BeNull($"{path} must not rely on inline styles");
            Regex.IsMatch(html, @"ds-dashgrid__cell ds-gc-\d+ ds-gs-\d+ ds-gy-\d+ ds-gr-\d+").Should().BeTrue($"{path} positions cells with classes");
        }
    }

    [Fact]
    public async Task NetworkOverview_PlacesSeverityBesideMessageRate_OnTheFirstRow()
    {
        WebAuthClient auth = await SignedInAsync("csp-rows");
        string list = await auth.Client.GetStringAsync("/dashboards");
        string[] paths = Regex.Matches(list, @"href=""(/dashboards/\d+)""").Select(m => m.Groups[1].Value).Distinct().ToArray();

        string? html = null;
        foreach (string path in paths)
        {
            string page = await auth.Client.GetStringAsync(path);
            if (page.Contains("Message rate", StringComparison.Ordinal) && page.Contains("Top talkers", StringComparison.Ordinal))
            {
                html = page;
                break;
            }
        }

        html.Should().NotBeNull("the Network Overview dashboard is shipped");
        html.Should().Contain("ds-gc-1 ds-gs-8 ds-gy-1", "Message rate is columns 1-8 of row 1")
            .And.Contain("ds-gc-9 ds-gs-4 ds-gy-1", "Severity distribution is columns 9-12 of the SAME row, not pushed below")
            .And.Contain("ds-gc-1 ds-gs-6 ds-gy-2", "Top talkers starts row 2");
    }

    [Fact]
    public async Task Stylesheet_DefinesEveryGridAndWidthClassTheMarkupCanEmit()
    {
        string css = await _factory.CreateClient().GetStringAsync("/ds.css");

        var missing = new List<string>();
        void Need(string cls)
        {
            if (!Regex.IsMatch(css, @"\." + Regex.Escape(cls) + @"\s*\{"))
            {
                missing.Add(cls);
            }
        }

        for (int i = 1; i <= WidgetLayout.Columns; i++)
        {
            Need($"ds-gc-{i}");
            Need($"ds-gs-{i}");
        }

        for (int i = 1; i <= 6; i++)
        {
            Need($"ds-gr-{i}");
        }

        for (int i = 1; i <= 30; i++)
        {
            Need($"ds-gy-{i}");
        }

        for (int i = 0; i <= 100; i++)
        {
            Need($"ds-pct-{i}");
        }

        missing.Should().BeEmpty("a class in the markup with no CSS rule is silently unstyled");
    }

    [Theory]
    [InlineData("/dashboards")]
    [InlineData("/search")]
    [InlineData("/devices")]
    [InlineData("/streams")]
    [InlineData("/rules")]
    [InlineData("/alerts")]
    [InlineData("/reports")]
    [InlineData("/monitoring")]
    [InlineData("/settings")]
    [InlineData("/audit")]
    [InlineData("/archives")]
    [InlineData("/about")]
    [InlineData("/help")]
    public async Task ServerRenderedPages_HaveNoInlineStyleAttributes(string path)
    {
        WebAuthClient auth = await SignedInAsync("csp-" + path.Trim('/').Replace('/', '-') + "x");

        string html = await auth.Client.GetStringAsync(path);

        Offender(html).Should().BeNull($"{path} must not rely on inline styles (blocked by CSP)");
    }

    /// <summary>The first element carrying an inline style attribute, or null — so a failure names it.</summary>
    private static string? Offender(string html)
    {
        Match m = Regex.Match(html, @"<[a-z0-9]+[^>]*sstyles*=[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Value[..Math.Min(m.Value.Length, 200)] : null;
    }
}
