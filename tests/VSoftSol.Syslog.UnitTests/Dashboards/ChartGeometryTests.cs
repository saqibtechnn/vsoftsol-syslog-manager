using FluentAssertions;
using VSoftSol.Syslog.Core.Dashboards;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Dashboards;

/// <summary>
/// PHASE_09 — the SVG geometry the widgets render from. Deterministic maths, so the
/// visual-regression snapshots stay byte-stable. Covers empty / single / huge / flat /
/// negative series (the phase's "empty, sparse, and extreme data" matrix, geometry half).
/// </summary>
public sealed class ChartGeometryTests
{
    [Fact]
    public void Decimate_ShortSeries_IsReturnedUnchanged()
    {
        double[] values = [1, 2, 3];
        ChartGeometry.Decimate(values, 10).Should().BeSameAs(values);
    }

    [Fact]
    public void Decimate_HugeSeries_IsReducedButKeepsEndpoints()
    {
        double[] values = [.. Enumerable.Range(0, 100_000).Select(i => (double)i)];
        IReadOnlyList<double> reduced = ChartGeometry.Decimate(values, 200);

        reduced.Count.Should().Be(200);
        reduced[0].Should().Be(0);
        reduced[^1].Should().Be(99_999);
        reduced.Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-5.0, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(7.0, 10.0)]
    [InlineData(23.0, 50.0)]
    [InlineData(4200.0, 5000.0)]
    [InlineData(150.0, 200.0)]
    public void NiceCeiling_RoundsUpToA125Multiple(double input, double expected)
    {
        ChartGeometry.NiceCeiling(input).Should().Be(expected);
    }

    [Fact]
    public void Polyline_EmptySeries_IsEmpty()
    {
        ChartGeometry.Polyline([], 100, 40, 10).Should().BeEmpty();
        ChartGeometry.AreaPath([], 100, 40, 10).Should().BeEmpty();
    }

    [Fact]
    public void Polyline_SingleValue_IsCentredFlat()
    {
        string points = ChartGeometry.Polyline([5], 100, 40, 10);
        points.Should().Be("50,20");
    }

    [Fact]
    public void Polyline_InvertsY_AndUsesInvariantDecimalPoint()
    {
        // max 10, value 10 → y 0 (top); value 0 → y 40 (bottom)
        string points = ChartGeometry.Polyline([10, 0, 5], 100, 40, 10);
        points.Should().Be("0,0 50,40 100,20");
    }

    [Fact]
    public void Polyline_ClampsOutOfRangeValues()
    {
        string points = ChartGeometry.Polyline([-3, 999], 10, 10, 10);
        points.Should().Be("0,10 10,0");
    }

    [Fact]
    public void AreaPath_ClosesToTheBaseline()
    {
        string d = ChartGeometry.AreaPath([10, 0], 100, 40, 10);
        d.Should().Be("M 0 0 L 100 40 L 100 40 L 0 40 Z");
    }

    [Fact]
    public void Donut_AllZero_ProducesNoSlices()
    {
        ChartGeometry.Donut([0, 0, 0], 50, 50, 40, 20).Should().BeEmpty();
    }

    [Fact]
    public void Donut_SplitsTheRingByWeight()
    {
        IReadOnlyList<ChartGeometry.DonutSlice> slices = ChartGeometry.Donut([1, 1, 2], 50, 50, 40, 20);

        slices.Should().HaveCount(3);
        slices[0].Fraction.Should().BeApproximately(0.25, 1e-9);
        slices[2].Fraction.Should().BeApproximately(0.50, 1e-9);
        slices.Sum(s => s.Fraction).Should().BeApproximately(1.0, 1e-9);
        slices[0].Path.Should().StartWith("M ").And.Contain("A ");
    }

    [Fact]
    public void Donut_SkipsZeroWeightCategories()
    {
        ChartGeometry.Donut([3, 0, 1], 50, 50, 40, 20).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0.0, -135.0)]
    [InlineData(50.0, 0.0)]
    [InlineData(100.0, 135.0)]
    [InlineData(200.0, 135.0)]  // clamped
    [InlineData(-10.0, -135.0)] // clamped
    public void GaugeAngle_MapsAcrossA270DegreeSweep(double value, double expected)
    {
        ChartGeometry.GaugeAngle(value, 0, 100).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void GaugeAngle_DegenerateRange_ReturnsTheFloor()
    {
        ChartGeometry.GaugeAngle(5, 10, 10).Should().Be(-135);
    }

    [Fact]
    public void Polyline_UsesAnInvariantDecimalPoint()
    {
        // The coordinate separator is ',' and the decimal separator must be '.', never a
        // locale comma — otherwise the SVG points string is corrupt. (The build VM runs in
        // globalization-invariant mode, so this asserts the format string directly rather
        // than by switching CurrentCulture.)
        string points = ChartGeometry.Polyline([2.5], 10, 10, 10);
        points.Should().Be("5,7.5");
        points.Split(',').Should().HaveCount(2);
    }
}
