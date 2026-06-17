using System.Linq;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class RegionInputFilterTests
{
    private static double[] Square(double size = 10.0) =>
        new[] { 0.0, 0.0, size, 0.0, size, size, 0.0, size };

    [Fact]
    public void KeepPointsInside_KeepsInside_DropsOutside()
    {
        double[] points =
        {
            5, 5, 0,     // inside
            1, 1, 0,     // inside
            50, 50, 0,   // far outside
            -20, 5, 0    // far outside
        };

        var keep = RegionInputFilter.KeepPointsInside(points, 4, new[] { Square() }, margin: 0.0);

        Assert.True(keep[0]);
        Assert.True(keep[1]);
        Assert.False(keep[2]);
        Assert.False(keep[3]);
    }

    [Fact]
    public void KeepPointsInside_MarginKeepsNearBoundaryPoints()
    {
        double[] points =
        {
            10.5, 5, 0,  // 0.5 outside the right edge
            13.0, 5, 0   // 3.0 outside
        };

        var keep = RegionInputFilter.KeepPointsInside(points, 2, new[] { Square() }, margin: 1.0);

        Assert.True(keep[0]);  // within margin
        Assert.False(keep[1]); // beyond margin
    }

    [Fact]
    public void KeepPointsInside_MultiplePolygons_KeepIfInAny()
    {
        double[] left = { 0, 0, 5, 0, 5, 5, 0, 5 };
        double[] right = { 20, 0, 25, 0, 25, 5, 20, 5 };
        double[] points =
        {
            2, 2, 0,    // in left
            22, 2, 0,   // in right
            12, 2, 0    // in the gap
        };

        var keep = RegionInputFilter.KeepPointsInside(points, 3, new[] { left, right }, margin: 0.0);

        Assert.True(keep[0]);
        Assert.True(keep[1]);
        Assert.False(keep[2]);
    }

    [Fact]
    public void KeepPointsInside_NoPolygons_KeepsAll()
    {
        double[] points = { 0, 0, 0, 100, 100, 0 };

        var keep = RegionInputFilter.KeepPointsInside(points, 2, System.Array.Empty<double[]>(), margin: 0.0);

        Assert.All(keep, k => Assert.True(k));
    }
}
