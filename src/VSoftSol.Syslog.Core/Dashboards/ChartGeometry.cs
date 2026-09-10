using System.Globalization;
using System.Text;

namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// Pure geometry for the server-rendered SVG widgets (PHASE_09 uses no client charting
/// library). Everything here is deterministic maths over a value array — the razor
/// components stay thin, and the visual-regression snapshots are stable because the numbers
/// come from here. All output is formatted in <see cref="CultureInfo.InvariantCulture"/> so
/// a decimal point is never a comma.
/// </summary>
public static class ChartGeometry
{
    /// <summary>
    /// Down-samples a long series to at most <paramref name="maxPoints"/> points by
    /// averaging fixed-size strides. A 100k-point series must still render smoothly
    /// (UX_STANDARDS §7). Preserves the first and last buckets exactly.
    /// </summary>
    public static IReadOnlyList<double> Decimate(IReadOnlyList<double> values, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (maxPoints < 2)
        {
            maxPoints = 2;
        }

        if (values.Count <= maxPoints)
        {
            return values;
        }

        var result = new double[maxPoints];
        double stride = (double)values.Count / maxPoints;
        for (int i = 0; i < maxPoints; i++)
        {
            int start = (int)(i * stride);
            int end = (int)((i + 1) * stride);
            if (end <= start)
            {
                end = start + 1;
            }

            end = Math.Min(end, values.Count);

            double sum = 0;
            for (int j = start; j < end; j++)
            {
                sum += values[j];
            }

            result[i] = sum / (end - start);
        }

        result[0] = values[0];
        result[^1] = values[^1];
        return result;
    }

    /// <summary>
    /// The maximum for a value axis: the smallest "nice" number (1/2/5 × 10ⁿ) at or above
    /// <paramref name="dataMax"/>. Returns 1 for a non-positive max so a flat-zero series
    /// still has a drawable axis.
    /// </summary>
    public static double NiceCeiling(double dataMax)
    {
        if (double.IsNaN(dataMax) || dataMax <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(dataMax)));
        double normalized = dataMax / magnitude;
        double nice = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        };

        return nice * magnitude;
    }

    /// <summary>
    /// Maps <paramref name="values"/> to an SVG <c>points</c> string inside a
    /// <paramref name="width"/>×<paramref name="height"/> box, y inverted (0 at the top).
    /// <paramref name="axisMax"/> fixes the scale; pass <see cref="NiceCeiling"/> of the
    /// data max. A single value renders as a flat line.
    /// </summary>
    public static string Polyline(IReadOnlyList<double> values, double width, double height, double axisMax)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return string.Empty;
        }

        double max = axisMax <= 0 ? 1 : axisMax;
        double stepX = values.Count == 1 ? 0 : width / (values.Count - 1);
        var sb = new StringBuilder(values.Count * 12);

        for (int i = 0; i < values.Count; i++)
        {
            double x = values.Count == 1 ? width / 2 : i * stepX;
            double y = height - Math.Clamp(values[i] / max, 0, 1) * height;
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(x.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(',')
              .Append(y.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>The <see cref="Polyline"/> closed down to the baseline — the <c>d</c> of a filled area path.</summary>
    public static string AreaPath(IReadOnlyList<double> values, double width, double height, double axisMax)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return string.Empty;
        }

        double max = axisMax <= 0 ? 1 : axisMax;
        double stepX = values.Count == 1 ? 0 : width / (values.Count - 1);
        var sb = new StringBuilder(values.Count * 14);

        for (int i = 0; i < values.Count; i++)
        {
            double x = values.Count == 1 ? width / 2 : i * stepX;
            double y = height - Math.Clamp(values[i] / max, 0, 1) * height;
            sb.Append(i == 0 ? "M " : " L ")
              .Append(x.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(' ')
              .Append(y.ToString("0.##", CultureInfo.InvariantCulture));
        }

        double lastX = values.Count == 1 ? width / 2 : width;
        sb.Append(" L ").Append(lastX.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(' ').Append(height.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(" L 0 ").Append(height.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(" Z");
        return sb.ToString();
    }

    /// <summary>One slice of a donut: the SVG arc path plus the mid-angle for label placement.</summary>
    public sealed record DonutSlice(string Path, double MidAngleDegrees, double Fraction);

    /// <summary>
    /// Turns category weights into donut slices around a ring of radius
    /// <paramref name="outerRadius"/> / <paramref name="innerRadius"/> centred at
    /// (<paramref name="cx"/>, <paramref name="cy"/>). Zero-weight categories are skipped.
    /// Starts at 12 o'clock, clockwise.
    /// </summary>
    public static IReadOnlyList<DonutSlice> Donut(
        IReadOnlyList<double> weights, double cx, double cy, double outerRadius, double innerRadius)
    {
        ArgumentNullException.ThrowIfNull(weights);
        double total = 0;
        foreach (double w in weights)
        {
            if (w > 0)
            {
                total += w;
            }
        }

        if (total <= 0)
        {
            return [];
        }

        var slices = new List<DonutSlice>(weights.Count);
        double angle = -90; // 12 o'clock
        foreach (double w in weights)
        {
            if (w <= 0)
            {
                continue;
            }

            double fraction = w / total;
            double sweep = fraction * 360;
            double start = angle;
            double end = angle + sweep;

            (double sox, double soy) = Polar(cx, cy, outerRadius, start);
            (double eox, double eoy) = Polar(cx, cy, outerRadius, end);
            (double six, double siy) = Polar(cx, cy, innerRadius, end);
            (double eix, double eiy) = Polar(cx, cy, innerRadius, start);
            int largeArc = sweep > 180 ? 1 : 0;

            string path = string.Create(CultureInfo.InvariantCulture,
                $"M {sox:0.##} {soy:0.##} A {outerRadius:0.##} {outerRadius:0.##} 0 {largeArc} 1 {eox:0.##} {eoy:0.##} " +
                $"L {six:0.##} {siy:0.##} A {innerRadius:0.##} {innerRadius:0.##} 0 {largeArc} 0 {eix:0.##} {eiy:0.##} Z");

            slices.Add(new DonutSlice(path, start + sweep / 2, fraction));
            angle = end;
        }

        return slices;
    }

    /// <summary>The needle angle for a gauge, in degrees from 12 o'clock, clamped to a 270° sweep (-135°..+135°).</summary>
    public static double GaugeAngle(double value, double min, double max)
    {
        if (max <= min)
        {
            return -135;
        }

        double t = Math.Clamp((value - min) / (max - min), 0, 1);
        return -135 + t * 270;
    }

    private static (double X, double Y) Polar(double cx, double cy, double radius, double angleDegrees)
    {
        double rad = angleDegrees * Math.PI / 180;
        return (cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }
}
