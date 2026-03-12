using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class TinEngineTests
{
    [Fact]
    public void Build_UnchangedInputs_ReturnsCachedResultInstance()
    {
        var engine = new TinEngine();
        double[] xy =
        {
            0.0, 0.0,
            10.0, 0.0,
            10.0, 10.0,
            0.0, 10.0
        };
        double[] z = { 0.0, 1.0, 2.0, 3.0 };

        TinResult? first = engine.Build(xy, z, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true);
        TinResult? second = engine.Build(xy, z, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Build_ZOnlyChange_ReusesTopologyArrays()
    {
        var engine = new TinEngine();
        double[] xy =
        {
            0.0, 0.0,
            10.0, 0.0,
            10.0, 10.0,
            0.0, 10.0
        };
        double[] z1 = { 0.0, 1.0, 2.0, 3.0 };
        double[] z2 = { 5.0, 6.0, 7.0, 8.0 };

        TinResult? first = engine.Build(xy, z1, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true);
        TinResult? second = engine.Build(xy, z2, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Same(first!.Faces, second!.Faces);
        Assert.Same(first.Edges, second.Edges);
        Assert.Equal(z2[0], second.Vertices[2]);
        Assert.Equal(z2[1], second.Vertices[5]);
        Assert.Equal(z2[2], second.Vertices[8]);
        Assert.Equal(z2[3], second.Vertices[11]);
    }
}
