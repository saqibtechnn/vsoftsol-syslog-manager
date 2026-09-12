namespace VSoftSol.Syslog.Core.Retention;

/// <summary>
/// A named retention starting point offered by the first-run wizard (PHASE_12 build item 2),
/// shown before any events have ever been ingested — so its projected disk usage is derived
/// from an assumed device count and message rate rather than measured data
/// (<see cref="RetentionEstimateReader"/> takes over once real traffic exists).
/// </summary>
public sealed record RetentionPreset(
    string Key, string Name, string Description, RetentionPolicy Policy, double AssumedDeviceCount);

/// <summary>Pure preset catalogue + projection, reusing <see cref="RetentionEstimator"/> with
/// assumed inputs. The assumed rate and average event size are also what the Sizing Guide's
/// worked examples use, so the wizard and the documentation never disagree.</summary>
public static class RetentionPresets
{
    /// <summary>A typical steady-state syslog rate per device; real deployments vary widely
    /// and incidents burst well above this, but it is a reasonable, clearly-labelled planning
    /// assumption for a server that has not received a single message yet.</summary>
    public const double AssumedEventsPerDevicePerMinute = 5.0;

    public static readonly RetentionPreset Small = new(
        "small", "Small", "Up to about 50 devices — a single site or branch office.",
        new RetentionPolicy { HotDays = 30, WarmDays = 60, ColdDays = 180 }, AssumedDeviceCount: 50);

    public static readonly RetentionPreset Medium = new(
        "medium", "Medium", "Up to about 200 devices — a campus or mid-size network.",
        new RetentionPolicy { HotDays = 30, WarmDays = 90, ColdDays = 365 }, AssumedDeviceCount: 200);

    public static readonly RetentionPreset Large = new(
        "large", "Large", "Up to about 1,000 devices — a large enterprise or multi-site estate.",
        new RetentionPolicy { HotDays = 14, WarmDays = 90, ColdDays = 730 }, AssumedDeviceCount: 1000);

    public static IReadOnlyList<RetentionPreset> All { get; } = [Small, Medium, Large];

    public static RetentionEstimate Estimate(RetentionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        double eventsPerDay = preset.AssumedDeviceCount * AssumedEventsPerDevicePerMinute * 60 * 24;
        return RetentionEstimator.Estimate(
            preset.Policy, eventsPerDay, RetentionEstimator.DefaultAvgEventBytes, RetentionEstimator.DefaultCompressionRatio);
    }
}
