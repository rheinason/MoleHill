using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class ZoneAnalysisCalculatorTests
{
    [RhinoNativeFact]
    public void Summarize_FlatSquare_ReturnsPlanSurfaceAndElevationMetrics()
    {
        using var mesh = new Mesh();
        mesh.Vertices.Add(0, 0, 10);
        mesh.Vertices.Add(2, 0, 10);
        mesh.Vertices.Add(2, 2, 10);
        mesh.Vertices.Add(0, 2, 10);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Faces.AddFace(0, 2, 3);
        mesh.Normals.ComputeNormals();

        var summary = ZoneAnalysisCalculator.Summarize(Guid.NewGuid(), new[] { mesh });

        Assert.Equal(4.0, summary.PlanArea, 8);
        Assert.Equal(4.0, summary.SurfaceArea, 8);
        Assert.Equal(10.0, summary.ElevationMinZ, 8);
        Assert.Equal(10.0, summary.ElevationAverageZ, 8);
        Assert.Equal(10.0, summary.ElevationMaxZ, 8);
        Assert.Equal(0.0, summary.SlopeAveragePercent, 8);
        Assert.Equal(2, summary.TriangleCount);
        Assert.Equal(1, summary.OutputCount);
        Assert.False(summary.HasEarthwork);
    }

    [RhinoNativeFact]
    public void Summarize_MultipleOutputs_AggregatesWithoutChangingZoneIdentity()
    {
        using var first = MakeTriangle(0, 0, 1);
        using var second = MakeTriangle(3, 0, 3);
        Guid id = Guid.NewGuid();

        var summary = ZoneAnalysisCalculator.Summarize(id, new[] { first, second });

        Assert.Equal(id, summary.ZoneId);
        Assert.Equal(2, summary.OutputCount);
        Assert.Equal(2, summary.TriangleCount);
        Assert.Equal(1.0, summary.PlanArea, 8);
        Assert.Equal(1.0, summary.ElevationAverageZ, 8);
        Assert.Equal(3.0, summary.ElevationMaxZ, 8);
    }

    private static Mesh MakeTriangle(double x, double y, double z)
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(x, y, z);
        mesh.Vertices.Add(x + 1, y, z);
        mesh.Vertices.Add(x, y + 1, z);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Normals.ComputeNormals();
        return mesh;
    }
}
