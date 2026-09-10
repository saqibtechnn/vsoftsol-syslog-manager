using System.Globalization;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Dashboards;

namespace VSoftSol.Syslog.Web.Dashboards;

/// <summary>One drawable series (a line / area) — dense over every bucket, gaps filled with zero.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values);

/// <summary>One bar / donut slice / table row.</summary>
public sealed record ChartCategory(string Label, double Value, double Fraction);

/// <summary>
/// Turns an <see cref="AggregationResult"/> into ready-to-draw numbers for the widget
/// components (PHASE_09 — the razor stays thin, the maths is here and unit-tested). Bucket
/// starts are returned as UTC instants; the component converts to local at render.
/// </summary>
public static class WidgetChartModel
{
    /// <summary>Time-series: one <see cref="ChartSeries"/> per group (or a single unnamed one), dense over the plan.</summary>
    public static (IReadOnlyList<ChartSeries> Series, IReadOnlyList<DateTimeOffset> BucketStartsUtc) Series(AggregationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Plan is not { } plan)
        {
            return ([], []);
        }

        var starts = new List<DateTimeOffset>(plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            starts.Add(plan.BucketStartUtc(i));
        }

        IReadOnlyList<string?> groups = result.GroupKeys();
        if (groups.Count == 0)
        {
            groups = [null];
        }

        var series = new List<ChartSeries>(groups.Count);
        foreach (string? group in groups)
        {
            var values = new double[plan.Count];
            foreach (AggPoint p in result.Points)
            {
                if (!string.Equals(p.GroupKey, group, StringComparison.Ordinal))
                {
                    continue;
                }

                if (p.BucketIndex is { } b && b >= 0 && b < plan.Count)
                {
                    values[b] += p.Value;
                }
            }

            series.Add(new ChartSeries(AggregationConstants.DisplayGroup(group), values));
        }

        return (series, starts);
    }

    /// <summary>Bars / table rows: one category per group, summed across buckets, largest first.</summary>
    public static IReadOnlyList<ChartCategory> Categories(AggregationResult result, bool severityLabels = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (AggPoint p in result.Points)
        {
            string key = p.GroupKey ?? AggregationConstants.NoneKey;
            totals[key] = totals.GetValueOrDefault(key) + p.Value;
        }

        double sum = totals.Values.Where(v => v > 0).Sum();
        return totals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ChartCategory(
                severityLabels ? AggregationConstants.DisplaySeverity(kv.Key) : AggregationConstants.DisplayGroup(kv.Key),
                kv.Value,
                sum > 0 ? kv.Value / sum : 0))
            .ToList();
    }

    /// <summary>Formats a metric value for a counter: bytes get a unit, everything else is grouped digits.</summary>
    public static string FormatValue(double value, string? unit)
    {
        if (unit == "bytes")
        {
            return FormatBytes(value);
        }

        return value >= 100 || value == Math.Floor(value)
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = Math.Abs(bytes);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(bytes < 0 ? -v : v):0.#} {units[u]}");
    }
}
