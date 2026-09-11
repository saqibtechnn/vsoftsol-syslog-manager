namespace VSoftSol.Syslog.Core.Retention;

/// <summary>Pure validation result — mirrors <c>Dashboards.WidgetValidator</c>'s shape.</summary>
public sealed record RetentionValidationResult
{
    public bool Ok { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static RetentionValidationResult Success { get; } = new() { Ok = true };

    public static RetentionValidationResult Fail(params string[] errors) => new() { Ok = false, Errors = errors };
}

/// <summary>
/// Validates retention configuration before it is saved (PHASE_10 build item 1). No I/O —
/// callers resolve the stream id and persist separately.
/// </summary>
public static class RetentionValidator
{
    public const int MaxDays = 3653; // ~10 years
    public const int MinCompressionLevel = 1;
    public const int MaxCompressionLevel = 19;
    public const int MaxArchivePathLength = 512;

    public static RetentionValidationResult Validate(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new List<string>();

        ValidateDays(policy.HotDays, "Hot days", errors);
        ValidateDays(policy.WarmDays, "Warm days", errors);
        ValidateDays(policy.ColdDays, "Cold days", errors);
        ValidateCompression(policy.CompressionLevel, errors);
        ValidateArchivePath(policy.ArchivePath, errors);

        return errors.Count == 0 ? RetentionValidationResult.Success : new RetentionValidationResult { Ok = false, Errors = errors };
    }

    public static RetentionValidationResult Validate(RetentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();

        ValidateDays(settings.DefaultHotDays, "Default hot days", errors);
        ValidateDays(settings.DefaultWarmDays, "Default warm days", errors);
        ValidateDays(settings.DefaultColdDays, "Default cold days", errors);
        ValidateCompression(settings.CompressionLevel, errors);
        ValidateArchivePath(settings.ArchiveRoot, errors);

        if (settings.BatchSize is < 1 or > 100_000)
        {
            errors.Add("Batch size must be between 1 and 100,000.");
        }

        return errors.Count == 0 ? RetentionValidationResult.Success : new RetentionValidationResult { Ok = false, Errors = errors };
    }

    private static void ValidateDays(int days, string label, List<string> errors)
    {
        if (days < 0)
        {
            errors.Add($"{label} cannot be negative.");
        }
        else if (days > MaxDays)
        {
            errors.Add($"{label} cannot exceed {MaxDays} days (about 10 years).");
        }
    }

    private static void ValidateCompression(int level, List<string> errors)
    {
        if (level is < MinCompressionLevel or > MaxCompressionLevel)
        {
            errors.Add($"Compression level must be between {MinCompressionLevel} and {MaxCompressionLevel}.");
        }
    }

    private static void ValidateArchivePath(string? path, List<string> errors)
    {
        if (string.IsNullOrEmpty(path))
        {
            return; // empty = use the default
        }

        if (path.Length > MaxArchivePathLength)
        {
            errors.Add($"Archive path cannot exceed {MaxArchivePathLength} characters.");
        }

        if (path.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add("Archive path contains control characters.");
        }
    }
}
