using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Dashboards;

/// <summary>How a widget's loaded data should be drawn.</summary>
public enum WidgetRenderKind
{
    Empty,
    Error,
    Scalar,
    Series,
    Groups,
    RecentEvents,
    DeviceGrid,
}

/// <summary>One row of a recent-events widget, already scope-checked.</summary>
public sealed record RecentEventRow(long EventId, DateTimeOffset ReceivedUtc, string? Hostname, int Severity, string Message);

/// <summary>One tile of a device-status widget.</summary>
public sealed record DeviceStatusRow(long DeviceId, string Name, DateTimeOffset? LastSeenUtc, string Status);

/// <summary>
/// The loaded, ready-to-render state of one widget. Every event query runs under the
/// viewer's <see cref="UserScope"/>, so a shared dashboard shows each viewer their own data
/// (PHASE_09 build item 7). Category labels in <see cref="Aggregation"/> may be
/// attacker-controlled hostnames — the razor encodes them at render.
/// </summary>
public sealed record WidgetData(
    WidgetRenderKind Kind,
    AggregationResult? Aggregation = null,
    IReadOnlyList<RecentEventRow>? Events = null,
    IReadOnlyList<DeviceStatusRow>? Devices = null,
    string? Message = null,
    bool Approximate = false)
{
    public static WidgetData Empty(string message) => new(WidgetRenderKind.Empty, Message: message);

    public static WidgetData Error(string message) => new(WidgetRenderKind.Error, Message: message);
}

/// <summary>
/// Resolves a <see cref="WidgetDefinition"/> and the viewer's scope into a
/// <see cref="WidgetData"/> (PHASE_09 — one data path). Aggregation results are served from
/// the scope-keyed <see cref="AggregationCache"/> so a wall dashboard does not hammer the
/// database.
/// </summary>
public sealed class WidgetDataService
{
    private readonly SqliteAggregationReader _aggregation;
    private readonly SystemSeriesReader _systemSeries;
    private readonly AggregationCache _cache;
    private readonly ScopedEventReader _events;
    private readonly SqliteSavedSearchStore _savedSearches;
    private readonly SqliteDeviceStore _devices;
    private readonly SqliteSearchFacets _facets;
    private readonly CurrentUserAccessor _users;
    private readonly TimeProvider _time;
    private readonly DashboardOptions _options;

    public WidgetDataService(
        SqliteAggregationReader aggregation,
        SystemSeriesReader systemSeries,
        AggregationCache cache,
        ScopedEventReader events,
        SqliteSavedSearchStore savedSearches,
        SqliteDeviceStore devices,
        SqliteSearchFacets facets,
        CurrentUserAccessor users,
        TimeProvider time,
        IOptions<DashboardOptions> options)
    {
        _aggregation = aggregation;
        _systemSeries = systemSeries;
        _cache = cache;
        _events = events;
        _savedSearches = savedSearches;
        _devices = devices;
        _facets = facets;
        _users = users;
        _time = time;
        _options = options.Value;
    }

    public async Task<WidgetData> LoadAsync(WidgetDefinition widget, TimeSpan dashboardRange, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(widget);
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        UserScope scope = user.Scope;

        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan range = widget.TimeRangeOverride ?? dashboardRange;
        if (range <= TimeSpan.Zero)
        {
            range = TimeSpan.FromHours(24);
        }

        DateTimeOffset from = now - range;

        try
        {
            return widget.Visualization switch
            {
                VisualizationType.RecentEventsTable => await RecentEventsAsync(scope, widget, from, now, ct).ConfigureAwait(false),
                VisualizationType.DeviceStatusGrid => await DeviceGridAsync(scope, now, ct).ConfigureAwait(false),
                _ when widget.Source.Kind == WidgetSourceKind.SystemSeries => await SystemAsync(widget, from, now, ct).ConfigureAwait(false),
                _ => await AggregateAsync(scope, widget, from, now, ct).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WidgetData.Error("This widget could not load. " + ex.Message);
        }
    }

    // ---- event aggregation --------------------------------------------------

    private async Task<WidgetData> AggregateAsync(
        UserScope scope, WidgetDefinition widget, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        string query = await ResolveQueryAsync(widget.Source, ct).ConfigureAwait(false);
        AggregationSpec spec = widget.Visualization == VisualizationType.SeverityDonut
            ? widget.Aggregation with { GroupByField = "severity", Bucket = BucketInterval.None }
            : widget.Aggregation;

        string key = AggregationCache.WidgetKey(widget.Source, spec, from, to, spec.Bucket) + "|q=" + query;

        SqliteAggregationReader.AggregationOutcome outcome = await _cache.GetOrAddAsync(
            scope, key,
            innerCt => _aggregation.AggregateAsync(scope, query, spec, from, to, spec.Bucket, innerCt),
            ct).ConfigureAwait(false);

        if (outcome.Status is SqliteAggregationReader.AggregationStatus.BadQuery
            or SqliteAggregationReader.AggregationStatus.BadAggregation)
        {
            return WidgetData.Error(outcome.Detail ?? "The widget query is no longer valid.");
        }

        if (outcome.Result.IsEmpty)
        {
            return WidgetData.Empty("No data in this time range.");
        }

        WidgetRenderKind kind = spec.Bucket != BucketInterval.None
            ? WidgetRenderKind.Series
            : spec.GroupByField is not null || widget.Visualization == VisualizationType.SeverityDonut
                ? WidgetRenderKind.Groups
                : WidgetRenderKind.Scalar;

        return new WidgetData(kind, Aggregation: outcome.Result, Approximate: outcome.Result.Truncated);
    }

    private async Task<WidgetData> SystemAsync(WidgetDefinition widget, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        SystemMetric metric = widget.Source.Metric ?? SystemMetric.IngestRate;

        if (widget.Visualization is VisualizationType.Counter or VisualizationType.RateGauge)
        {
            CollectorSample? latest = await _systemSeries.LatestAsync(ct).ConfigureAwait(false);
            if (latest is null)
            {
                return WidgetData.Empty("Collector metrics will appear once the collector has run.");
            }

            double value;
            if (metric == SystemMetric.IngestRate)
            {
                IReadOnlyList<AggPoint> pts = (await _systemSeries
                    .SeriesAsync(metric, to.AddMinutes(-10), to, BucketInterval.OneMinute, ct).ConfigureAwait(false)).Points;
                value = pts.Count > 0 ? pts[^1].Value : 0;
            }
            else
            {
                value = latest.ValueOf(metric);
            }

            return new WidgetData(WidgetRenderKind.Scalar, Aggregation: new AggregationResult
            {
                Points = [new AggPoint(null, null, value)],
                Plan = null,
                Truncated = false,
                GroupsOmitted = 0,
            });
        }

        AggregationResult series = await _systemSeries.SeriesAsync(
            metric, from, to, widget.Aggregation.Bucket == BucketInterval.None ? BucketInterval.Auto : widget.Aggregation.Bucket, ct)
            .ConfigureAwait(false);

        return series.IsEmpty
            ? WidgetData.Empty("Collector metrics will appear once the collector has run.")
            : new WidgetData(WidgetRenderKind.Series, Aggregation: series);
    }

    // ---- list widgets -----------------------------------------------------

    private async Task<WidgetData> RecentEventsAsync(
        UserScope scope, WidgetDefinition widget, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        string query = await ResolveQueryAsync(widget.Source, ct).ConfigureAwait(false);
        var request = new SearchRequest
        {
            QueryText = query,
            FromUtc = from,
            ToUtc = to,
            Limit = _options.RecentEventsLimit,
            Sort = SearchSort.Default,
        };

        SearchResult result = await _events.SearchAsync(scope, request, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return WidgetData.Error("The widget query is no longer valid.");
        }

        if (result.Rows.Count == 0)
        {
            return WidgetData.Empty("No matching events in this time range.");
        }

        var rows = result.Rows
            .Select(e => new RecentEventRow(e.EventId, e.ReceivedUtc, e.Hostname, (int)e.Severity, e.Message))
            .ToList();

        return new WidgetData(WidgetRenderKind.RecentEvents, Events: rows);
    }

    private async Task<WidgetData> DeviceGridAsync(UserScope scope, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<SqliteSearchFacets.FacetValue> visible =
            await _facets.ListDevicesAsync(scope, ct).ConfigureAwait(false);
        if (visible.Count == 0)
        {
            return WidgetData.Empty("No devices are visible to you yet.");
        }

        var visibleIds = visible.Select(v => v.Id).ToHashSet();
        IReadOnlyList<Device> devices = await _devices.ListAsync([DeviceApprovalStatus.Approved], ct).ConfigureAwait(false);

        var rows = devices
            .Where(d => visibleIds.Contains(d.DeviceId))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeviceStatusRow(d.DeviceId, d.Name, d.LastSeenUtc, DeviceStatus(d, now)))
            .ToList();

        return rows.Count == 0
            ? WidgetData.Empty("No devices are visible to you yet.")
            : new WidgetData(WidgetRenderKind.DeviceGrid, Devices: rows);
    }

    private static string DeviceStatus(Device device, DateTimeOffset now)
    {
        if (device.LastSeenUtc is not { } seen)
        {
            return "silent";
        }

        double minutes = (now - seen).TotalMinutes;
        int heartbeat = device.HeartbeatMinutes ?? 15;
        return minutes > heartbeat * 3 ? "silent"
            : minutes > heartbeat ? "quiet"
            : "ok";
    }

    // ---- shared ---------------------------------------------------------

    private async Task<string> ResolveQueryAsync(WidgetSource source, CancellationToken ct)
    {
        if (source is { Kind: WidgetSourceKind.EventQuery, SavedSearchId: { } sid })
        {
            return await _savedSearches.GetQueryTextAsync(sid, ct).ConfigureAwait(false) ?? string.Empty;
        }

        return source.InlineQuery ?? string.Empty;
    }
}
