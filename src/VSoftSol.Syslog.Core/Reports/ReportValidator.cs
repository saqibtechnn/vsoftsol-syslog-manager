namespace VSoftSol.Syslog.Core.Reports;

/// <summary>Pure validation result — mirrors <c>Retention.RetentionValidationResult</c>'s shape.</summary>
public sealed record ReportValidationResult
{
    public bool Ok { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ReportValidationResult Success { get; } = new() { Ok = true };
}

/// <summary>Pure validation for a <see cref="ReportDefinition"/> before it is saved
/// (PHASE_10 build item 6/8). No I/O — the template-key existence check is against the
/// static <see cref="CannedReportCatalog"/>, not a database lookup.</summary>
public static class ReportValidator
{
    public const int MaxNameLength = 120;
    public const int MinTimeRangeDays = 1;
    public const int MaxTimeRangeDays = 3653;
    public const int MaxRecipients = 25;
    public const int MaxFolderPathLength = 512;

    public static ReportValidationResult Validate(ReportDefinition report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(report.Name))
        {
            errors.Add("Name is required.");
        }
        else if (report.Name.Length > MaxNameLength)
        {
            errors.Add($"Name cannot exceed {MaxNameLength} characters.");
        }

        if (report.TimeRangeDays is < MinTimeRangeDays or > MaxTimeRangeDays)
        {
            errors.Add($"Time range must be between {MinTimeRangeDays} and {MaxTimeRangeDays} days.");
        }

        if (report.IsCustom)
        {
            if (report.SavedSearchId is null && string.IsNullOrWhiteSpace(report.QueryText))
            {
                errors.Add("A custom report needs a saved search or an inline query.");
            }
        }
        else if (CannedReportCatalog.Find(report.TemplateKey) is null)
        {
            errors.Add($"'{report.TemplateKey}' is not a known report template.");
        }

        ValidateDelivery(report.Schedule, report.Delivery, errors);

        return errors.Count == 0
            ? ReportValidationResult.Success
            : new ReportValidationResult { Ok = false, Errors = errors };
    }

    private static void ValidateDelivery(ReportSchedule schedule, ReportDeliveryConfig delivery, List<string> errors)
    {
        if (schedule == ReportSchedule.None)
        {
            return; // on-demand only — delivery config is irrelevant
        }

        switch (delivery.Type)
        {
            case ReportDeliveryType.None:
                errors.Add("A scheduled report needs a delivery method (email or folder).");
                break;

            case ReportDeliveryType.Email:
                if (delivery.Recipients.Count == 0)
                {
                    errors.Add("Email delivery needs at least one recipient.");
                }
                else if (delivery.Recipients.Count > MaxRecipients)
                {
                    errors.Add($"Email delivery supports at most {MaxRecipients} recipients.");
                }
                else
                {
                    foreach (string recipient in delivery.Recipients)
                    {
                        if (!IsPlausibleEmail(recipient))
                        {
                            errors.Add($"'{recipient}' is not a valid email address.");
                        }
                    }
                }

                break;

            case ReportDeliveryType.Folder:
                if (string.IsNullOrWhiteSpace(delivery.FolderPath))
                {
                    errors.Add("Folder delivery needs a destination path.");
                }
                else if (delivery.FolderPath.Length > MaxFolderPathLength)
                {
                    errors.Add($"Folder path cannot exceed {MaxFolderPathLength} characters.");
                }
                else if (delivery.FolderPath.IndexOfAny(['\0', '\r', '\n']) >= 0)
                {
                    errors.Add("Folder path contains control characters.");
                }

                break;
        }
    }

    private static bool IsPlausibleEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        int at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < value.Length - 1 && value.IndexOf('.', at) > at;
    }
}
