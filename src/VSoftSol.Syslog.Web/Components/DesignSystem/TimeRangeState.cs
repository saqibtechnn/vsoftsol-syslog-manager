namespace VSoftSol.Syslog.Web.Components.DesignSystem;

/// <summary>A named relative window, or an explicit absolute range.</summary>
public sealed record TimeRange(string Label, TimeSpan? Relative, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc)
{
    public static readonly IReadOnlyList<TimeRange> Presets =
    [
        new("Last 15 minutes", TimeSpan.FromMinutes(15), null, null),
        new("Last hour", TimeSpan.FromHours(1), null, null),
        new("Last 4 hours", TimeSpan.FromHours(4), null, null),
        new("Last 24 hours", TimeSpan.FromHours(24), null, null),
        new("Last 7 days", TimeSpan.FromDays(7), null, null),
        new("Last 30 days", TimeSpan.FromDays(30), null, null),
    ];

    public (DateTimeOffset From, DateTimeOffset To) Resolve(DateTimeOffset nowUtc) =>
        Relative is { } window ? (nowUtc - window, nowUtc) : (FromUtc ?? nowUtc - TimeSpan.FromHours(1), ToUtc ?? nowUtc);
}

/// <summary>
/// The global time-range picker's shared state (UX_STANDARDS.md §6: "means the same thing
/// everywhere"). Scoped per circuit; pages subscribe to <see cref="Changed"/>.
/// </summary>
public sealed class TimeRangeState
{
    public event Action? Changed;

    public TimeRange Current { get; private set; } = TimeRange.Presets[3]; // Last 24 hours

    public void Set(TimeRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range != Current)
        {
            Current = range;
            Changed?.Invoke();
        }
    }
}
