using MoleHill.Core.Grading;
using Xunit;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Tests;

public class PolygonInteriorPointTests
{
    [Fact]
    public void PolygonInteriorPoint_ConvexSquare_ReturnsVertexAverage()
    {
        double[] xy = { 0, 0, 10, 0, 10, 10, 0, 10 };

        (double x, double y) = Geometry2D.PolygonInteriorPoint(xy, 4);

        Assert.Equal(5.0, x, 12);
        Assert.Equal(5.0, y, 12);
    }

    [Fact]
    public void PolygonInteriorPoint_UShapedPolygon_ReturnsPointInsideFootprint()
    {
        // U-shape: 10x10 outline with an open notch (2,2)-(8,10). The vertex average lands at
        // (5, 5.5), inside the notch — OUTSIDE the polygon.
        double[] xy = { 0, 0, 10, 0, 10, 10, 8, 10, 8, 2, 2, 2, 2, 10, 0, 10 };

        Assert.False(Geometry2D.PointInPolygon(5.0, 5.5, xy, 8));

        (double x, double y) = Geometry2D.PolygonInteriorPoint(xy, 8);

        Assert.True(Geometry2D.PointInPolygon(x, y, xy, 8));
    }

    [Fact]
    public void PolygonInteriorPoint_UShapedPolygonClockwise_ReturnsPointInsideFootprint()
    {
        // Same U-shape with reversed winding — the construction must be winding-agnostic.
        double[] xy = { 0, 10, 2, 10, 2, 2, 8, 2, 8, 10, 10, 10, 10, 0, 0, 0 };

        Assert.False(Geometry2D.PointInPolygon(5.0, 5.5, xy, 8));

        (double x, double y) = Geometry2D.PolygonInteriorPoint(xy, 8);

        Assert.True(Geometry2D.PointInPolygon(x, y, xy, 8));
    }

    [Fact]
    public void PolygonInteriorPoint_LShapedPolygon_ReturnsPointInsideFootprint()
    {
        // Thin L: long arms make the vertex average fall outside the inner corner.
        double[] xy = { 0, 0, 20, 0, 20, 2, 2, 2, 2, 20, 0, 20 };

        (double avgInsideX, double avgInsideY) = (7.333, 7.333);
        Assert.False(Geometry2D.PointInPolygon(avgInsideX, avgInsideY, xy, 6));

        (double x, double y) = Geometry2D.PolygonInteriorPoint(xy, 6);

        Assert.True(Geometry2D.PointInPolygon(x, y, xy, 6));
    }

    [Fact]
    public void PolygonInteriorPoint_DegenerateCollinear_DoesNotThrow()
    {
        double[] xy = { 0, 0, 5, 0, 10, 0 };

        (double x, double y) = Geometry2D.PolygonInteriorPoint(xy, 3);

        Assert.False(double.IsNaN(x));
        Assert.False(double.IsNaN(y));
    }
}
