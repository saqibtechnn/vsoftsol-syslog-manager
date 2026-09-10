using System.Runtime.CompilerServices;
using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Components.Pages.Dashboards.Widgets;
using VSoftSol.Syslog.Web.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 Validation — "every widget renders an empty state, never an exception" and the
/// visual-regression snapshot ("a styling change cannot silently break a chart"). The
/// presentational widget components are pure, so they render with a bare service provider.
/// Snapshots live under <c>tests/fixtures/dashboards/</c>; a diff means the render changed.
/// </summary>
public sealed class WidgetComponentTests
{
    private static readonly string SnapshotDir = ResolveSnapshotDir();

    private static string ResolveSnapshotDir()
    {
        // Walk up from the test assembly's output dir to the repo root (the folder holding
        // the .sln). PathMap makes [CallerFilePath] unusable here, so anchor on the file
        // system instead.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VSoftSol.Syslog.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate the repo root (VSoftSol.Syslog.sln) from " + AppContext.BaseDirectory);
        }

        return Path.Combine(dir.FullName, "tests", "fixtures", "dashboards");
    }

    private static AggregationResult Series()
    {
        BucketPlan plan = TimeBucketing.Plan(
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero),
            BucketInterval.OneHour);

        return new AggregationResult
        {
            Points =
            [
                new AggPoint(null, 0, 10), new AggPoint(null, 1, 25), new AggPoint(null, 2, 8),
                new AggPoint(null, 3, 40), new AggPoint(null, 4, 15), new AggPoint(null, 5, 22),
            ],
            Plan = plan,
        };
    }

    private static AggregationResult Groups() => new()
    {
        Points =
        [
            new AggPoint("web-1", null, 120),
            new AggPoint("web-2", null, 60),
            new AggPoint("db-1", null, 18),
        ],
        Plan = null,
    };

    private static AggregationResult SeverityGroups() => new()
    {
        Points =
        [
            new AggPoint("2", null, 5),   // Critical
            new AggPoint("4", null, 40),  // Warning
            new AggPoint("6", null, 200), // Informational
        ],
        Plan = null,
    };

    // ---- empty states ---------------------------------------------------

    public static IEnumerable<object[]> EmptyCases()
    {
        yield return [typeof(TimeSeriesChart), new Dictionary<string, object?> { ["Result"] = AggregationResult.Empty }];
        yield return [typeof(BarChart), new Dictionary<string, object?> { ["Result"] = AggregationResult.Empty }];
        yield return [typeof(TopNTable), new Dictionary<string, object?> { ["Result"] = AggregationResult.Empty }];
        yield return [typeof(SeverityDonut), new Dictionary<string, object?> { ["Result"] = AggregationResult.Empty }];
        yield return [typeof(CounterWidget), new Dictionary<string, object?> { ["Value"] = 0.0 }];
        yield return [typeof(RateGauge), new Dictionary<string, object?> { ["Value"] = 0.0 }];
        yield return [typeof(RecentEventsTable), new Dictionary<string, object?> { ["Rows"] = (IReadOnlyList<RecentEventRow>)[] }];
        yield return [typeof(DeviceStatusGrid), new Dictionary<string, object?> { ["Rows"] = (IReadOnlyList<DeviceStatusRow>)[] }];
    }

    [Theory]
    [MemberData(nameof(EmptyCases))]
    public async Task EveryWidget_WithNoData_RendersWithoutThrowing(Type component, Dictionary<string, object?> parameters)
    {
        string html = await Render(component, parameters);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().NotContain("System.");
    }

    [Fact]
    public async Task ExtremeData_100kPointSeries_StillRenders()
    {
        BucketPlan plan = TimeBucketing.Plan(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(100_000 * 60),
            BucketInterval.OneMinute);

        var rng = new Random(20260911);
        var points = Enumerable.Range(0, Math.Min(plan.Count, 100_000))
            .Select(i => new AggPoint(null, i, rng.Next(0, 500)))
            .ToList();

        string html = await RazorRenderer.RenderAsync<TimeSeriesChart>(new()
        {
            ["Result"] = new AggregationResult { Points = points, Plan = plan },
        });

        html.Should().Contain("<polyline");
    }

    [Fact]
    public async Task AllIdenticalValues_And_NegativeValues_DoNotThrow()
    {
        AggregationResult flat = new()
        {
            Points = Enumerable.Range(0, 6).Select(i => new AggPoint(null, i, 7.0)).ToList(),
            Plan = Series().Plan,
        };
        (await RazorRenderer.RenderAsync<TimeSeriesChart>(new() { ["Result"] = flat })).Should().Contain("polyline");

        AggregationResult negatives = new()
        {
            Points = [new AggPoint("a", null, -5), new AggPoint("b", null, 0)],
            Plan = null,
        };
        (await RazorRenderer.RenderAsync<BarChart>(new() { ["Result"] = negatives })).Should().NotBeNullOrWhiteSpace();
    }

    // ---- visual regression --------------------------------------------

    public static IEnumerable<object[]> SnapshotCases()
    {
        yield return ["timeseries-line", typeof(TimeSeriesChart), new Dictionary<string, object?> { ["Result"] = Series(), ["Area"] = false }];
        yield return ["timeseries-area", typeof(TimeSeriesChart), new Dictionary<string, object?> { ["Result"] = Series(), ["Area"] = true }];
        yield return ["bar", typeof(BarChart), new Dictionary<string, object?> { ["Result"] = Groups(), ["TopN"] = 10 }];
        yield return ["topn", typeof(TopNTable), new Dictionary<string, object?> { ["Result"] = Groups(), ["TopN"] = 10 }];
        yield return ["donut", typeof(SeverityDonut), new Dictionary<string, object?> { ["Result"] = SeverityGroups() }];
        yield return ["counter", typeof(CounterWidget), new Dictionary<string, object?> { ["Value"] = 1234.0, ["Unit"] = "bytes" }];
        yield return ["gauge", typeof(RateGauge), new Dictionary<string, object?> { ["Value"] = 42.0, ["Max"] = 100.0 }];
    }

    [Theory]
    [MemberData(nameof(SnapshotCases))]
    public async Task Widget_RendersPixelStable(string name, Type component, Dictionary<string, object?> parameters)
    {
        string html = Normalize(await Render(component, parameters));
        string path = Path.Combine(SnapshotDir, name + ".snapshot.html");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(SnapshotDir);
            await File.WriteAllTextAsync(path, html);
            Assert.Fail($"Snapshot '{name}' did not exist and was written. Re-run to verify, then commit it.");
        }

        string expected = Normalize(await File.ReadAllTextAsync(path));
        html.Should().Be(expected, $"the rendered output of '{name}' changed — update {name}.snapshot.html deliberately if intended");
    }

    private static Task<string> Render(Type component, Dictionary<string, object?> parameters) => component switch
    {
        _ when component == typeof(TimeSeriesChart) => RazorRenderer.RenderAsync<TimeSeriesChart>(parameters),
        _ when component == typeof(BarChart) => RazorRenderer.RenderAsync<BarChart>(parameters),
        _ when component == typeof(TopNTable) => RazorRenderer.RenderAsync<TopNTable>(parameters),
        _ when component == typeof(SeverityDonut) => RazorRenderer.RenderAsync<SeverityDonut>(parameters),
        _ when component == typeof(CounterWidget) => RazorRenderer.RenderAsync<CounterWidget>(parameters),
        _ when component == typeof(RateGauge) => RazorRenderer.RenderAsync<RateGauge>(parameters),
        _ when component == typeof(RecentEventsTable) => RazorRenderer.RenderAsync<RecentEventsTable>(parameters),
        _ when component == typeof(DeviceStatusGrid) => RazorRenderer.RenderAsync<DeviceStatusGrid>(parameters),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static string Normalize(string html)
    {
        html = html.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        // The time axis renders bucket starts in the machine's local timezone; that is
        // correct for a user but not deterministic across machines (TESTING_STANDARDS §2.3).
        // Redact just the axis label text — the polyline geometry, which is what visual
        // regression protects, stays byte-checked.
        html = System.Text.RegularExpressions.Regex.Replace(
            html, "(<div class=\"ds-chart__axis\">).*?(</div>)", "$1<span>#</span>$2",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return html;
    }
}
