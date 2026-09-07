using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Components.Layout;

/// <summary>One primary navigation entry.</summary>
public sealed record NavItem(string Label, string Href, string Icon, string Policy);

/// <summary>
/// The fixed navigation, in order (PHASE_04 build item 10; UX_STANDARDS.md §6 — one
/// navigation structure, consistent across every page).
/// </summary>
public static class NavItems
{
    public static IReadOnlyList<NavItem> Primary { get; } =
    [
        new("Dashboards", "/dashboards", "▤", AuthPolicies.ViewData),
        new("Search", "/search", "🔍", AuthPolicies.ViewData),
        new("Devices", "/devices", "🖧", AuthPolicies.ViewData),
        new("Streams", "/streams", "≋", AuthPolicies.ViewData),
        new("Rules", "/rules", "⚙", AuthPolicies.Operate),
        new("Alerts", "/alerts", "🔔", AuthPolicies.Operate),
        new("Reports", "/reports", "▦", AuthPolicies.ViewReports),
        new("Settings", "/settings", "⚙", AuthPolicies.Administer),
    ];
}
