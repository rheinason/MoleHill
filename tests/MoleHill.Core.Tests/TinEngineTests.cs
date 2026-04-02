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

    [Fact]
    public void Build_AllNaNInput_PreservesUnresolvedVertexZAsNaN()
    {
        var engine = new TinEngine();
        double[] xy =
        {
            0.0, 0.0,
            10.0, 0.0,
            10.0, 10.0,
            0.0, 10.0
        };
        double[] z = { double.NaN, double.NaN, double.NaN, double.NaN };

        TinResult? result = engine.Build(xy, z, Array.Empty<int>(), QualitySettings.None, out string? error, useConvexHull: true);

        Assert.Null(error);
        Assert.NotNull(result);
        for (int i = 0; i < result!.VertexCount; i++)
            Assert.True(double.IsNaN(result.Vertices[i * 3 + 2]), $"Expected vertex {i} to remain unresolved (NaN).");
    }

    [Fact]
    public void Build_MaxBoundaryEdgeLengthDisabled_KeepsBoundarySliver()
    {
        var engine = new TinEngine();
        double[] xy =
        {
            0.0, 0.0,
            1.0, 0.0,
            0.0, 0.02,
            100.0, 0.0001
        };
        double[] z = { 0.0, 0.0, 0.0, 0.0 };

        TinResult? result = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? error,
            useConvexHull: true,
            maxBoundaryEdgeLength: -1);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(2, result!.FaceCount);
    }

    [Fact]
    public void Build_MaxBoundaryEdgeLengthAuto_RemovesBoundarySliverAndRebuildsTopology()
    {
        var engine = new TinEngine();
        double[] xy =
        {
            0.0, 0.0,
            1.0, 0.0,
            0.0, 0.02,
            100.0, 0.0001
        };
        double[] z = { 0.0, 0.0, 0.0, 0.0 };

        TinResult? result = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? error,
            useConvexHull: true,
            maxBoundaryEdgeLength: 0);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(1, result!.FaceCount);
        Assert.Equal(3, result.EdgeCount);
        Assert.Equal(3, result.NakedEdgeCount);
    }

    [Fact]
    public void Build_MaxBoundaryEdgeLengthChange_InvalidatesCachedTopologyResult()
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

        TinResult? first = engine.Build(xy, z, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true, maxBoundaryEdgeLength: 0);
        TinResult? second = engine.Build(xy, z, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true, maxBoundaryEdgeLength: 5.0);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}
