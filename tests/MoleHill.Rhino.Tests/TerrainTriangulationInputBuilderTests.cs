using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainTriangulationInputBuilderTests
{
    [RhinoNativeFact]
    public void CreateTriangulationPolylines_TessellatedContour_PreservesSourceStations()
    {
        const int pointCount = 1_000;
        var polyline = new Polyline(pointCount);
        for (int i = 0; i < pointCount; i++)
            polyline.Add(i * 50.0, Math.Sin(i * 0.05) * 10.0, 100.0);
        using var curve = new PolylineCurve(polyline);

        List<double[]> result = TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            Array.Empty<Curve>(),
            new Curve[] { curve },
            tolerance: 0.0125);

        double[] output = Assert.Single(result);
        Assert.Equal(pointCount, output.Length / 3);
    }

    [RhinoNativeFact]
    public void CreateTriangulationPolylines_LongStraightBreakline_AddsIntermediateStations()
    {
        using var curve = new LineCurve(
            new Point3d(0.0, 0.0, 100.0),
            new Point3d(100.0, 0.0, 100.0));

        List<double[]> result = TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            new Curve[] { curve },
            Array.Empty<Curve>(),
            tolerance: 0.5);

        double[] output = Assert.Single(result);
        Assert.True(output.Length / 3 > 2);
        Assert.Equal(0.0, output[0], 6);
        Assert.Equal(100.0, output[^3], 6);
    }
}
