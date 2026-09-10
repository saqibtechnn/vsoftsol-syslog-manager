namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// Validates a whole <see cref="DashboardDefinition"/> before it is stored: name, widget
/// count, unique widget ids, every widget valid, and a layout that refers only to real
/// widgets. Pure.
/// </summary>
public static class DashboardValidator
{
    public const int MaxWidgets = 24;

    public const int MaxNameLength = 120;

    public static ValidationResult Validate(DashboardDefinition dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(dashboard.Name))
        {
            errors.Add("The dashboard needs a name.");
        }
        else if (dashboard.Name.Length > MaxNameLength)
        {
            errors.Add($"The dashboard name must be {MaxNameLength} characters or fewer.");
        }

        if (dashboard.Description is { Length: > 500 })
        {
            errors.Add("The dashboard description must be 500 characters or fewer.");
        }

        if (dashboard.DefaultTimeRange <= TimeSpan.Zero || dashboard.DefaultTimeRange > TimeSpan.FromDays(366))
        {
            errors.Add("The default time range must be between 1 minute and 366 days.");
        }

        if (dashboard.RefreshInterval < TimeSpan.Zero)
        {
            errors.Add("The refresh interval cannot be negative.");
        }

        if (dashboard.Widgets.Count > MaxWidgets)
        {
            errors.Add($"A dashboard can hold at most {MaxWidgets} widgets.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (WidgetDefinition widget in dashboard.Widgets)
        {
            if (!ids.Add(widget.Id))
            {
                errors.Add($"Two widgets share the id '{widget.Id}'.");
            }

            ValidationResult w = WidgetValidator.Validate(widget);
            if (!w.Ok)
            {
                foreach (string e in w.Errors)
                {
                    errors.Add($"{Describe(widget)}: {e}");
                }
            }
        }

        foreach (WidgetLayout layout in dashboard.Layout)
        {
            if (!ids.Contains(layout.WidgetId))
            {
                errors.Add($"The layout places a widget ('{layout.WidgetId}') that does not exist.");
            }
        }

        return ValidationResult.From(errors);
    }

    private static string Describe(WidgetDefinition widget) =>
        string.IsNullOrWhiteSpace(widget.Title) ? "A widget" : $"Widget '{widget.Title}'";
}
