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
    public void Build_ZOnlyChangeWithCrossingBreaklines_ReinterpolatesSteinerZ()
    {
        // Two sparse two-point breaklines crossing at the origin: the crossing is a Steiner point.
        double[] xy =
        {
            -20.0, -20.0, 20.0, -20.0, 20.0, 20.0, -20.0, 20.0,
            -15.0, 0.0, 15.0, 0.0,
            0.0, -15.0, 0.0, 15.0
        };
        int[] segments = { 4, 5, 6, 7 };
        double[] z1 = new double[8];
        double[] z2 = { 0.0, 0.0, 0.0, 0.0, 10.0, 10.0, 0.0, 0.0 };

        var engine = new TinEngine();
        TinResult? first = engine.Build(xy, z1, segments, QualitySettings.None, out _, useConvexHull: true);
        TinResult? edited = engine.Build(xy, z2, segments, QualitySettings.None, out _, useConvexHull: true);
        TinResult? fresh = new TinEngine().Build(xy, z2, segments, QualitySettings.None, out _, useConvexHull: true);

        Assert.NotNull(first);
        Assert.NotNull(edited);
        Assert.NotNull(fresh);
        Assert.Contains(first!.SourceIds, id => id >= 8);
        Assert.Equal(fresh!.Vertices, edited!.Vertices);
    }

    [Fact]
    public void Build_WithoutEdgeTopology_ReturnsFacesWithoutEdgeArrays()
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

        TinResult? result = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? error,
            useConvexHull: true,
            boundaryPeelSettings: BoundaryTrianglePeelSettings.Disabled,
            includeEdgeTopology: false);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(2, result!.FaceCount);
        Assert.Equal(0, result.EdgeCount);
        Assert.Empty(result.Edges);
        Assert.Equal(0, result.NakedEdgeCount);
        Assert.Empty(result.NakedEdges);
    }

    [Fact]
    public void Build_ChangingEdgeTopologyMode_DoesNotReuseIncompatibleCachedResult()
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

        TinResult? withEdges = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? withEdgesError,
            useConvexHull: true,
            boundaryPeelSettings: BoundaryTrianglePeelSettings.Disabled,
            includeEdgeTopology: true);
        TinResult? withoutEdges = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? withoutEdgesError,
            useConvexHull: true,
            boundaryPeelSettings: BoundaryTrianglePeelSettings.Disabled,
            includeEdgeTopology: false);
        TinResult? withoutEdgesCached = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? cachedError,
            useConvexHull: true,
            boundaryPeelSettings: BoundaryTrianglePeelSettings.Disabled,
            includeEdgeTopology: false);

        Assert.Null(withEdgesError);
        Assert.Null(withoutEdgesError);
        Assert.Null(cachedError);
        Assert.NotNull(withEdges);
        Assert.NotNull(withoutEdges);
        Assert.NotSame(withEdges, withoutEdges);
        Assert.True(withEdges!.EdgeCount > 0);
        Assert.Equal(0, withoutEdges!.EdgeCount);
        Assert.Same(withoutEdges, withoutEdgesCached);
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
    public void Build_AutoBoundaryPeelWithoutEdgeOutput_MatchesGenericTopologyOutput()
    {
        double[] xy =
        {
            0.0, 0.0,
            1.0, 0.0,
            0.0, 0.02,
            100.0, 0.0001
        };
        double[] z = { 0.0, 0.0, 0.0, 0.0 };

        TinResult? withEdges = new TinEngine().Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? withEdgesError,
            useConvexHull: true,
            maxBoundaryEdgeLength: 0,
            includeEdgeTopology: true);
        TinResult? withoutEdges = new TinEngine().Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? withoutEdgesError,
            useConvexHull: true,
            maxBoundaryEdgeLength: 0,
            includeEdgeTopology: false);

        Assert.Null(withEdgesError);
        Assert.Null(withoutEdgesError);
        Assert.NotNull(withEdges);
        Assert.NotNull(withoutEdges);
        Assert.Equal(withEdges!.Vertices, withoutEdges!.Vertices);
        Assert.Equal(withEdges.SourceIds, withoutEdges.SourceIds);
        Assert.Equal(withEdges.Faces, withoutEdges.Faces);
        Assert.Empty(withoutEdges.Edges);
        Assert.Empty(withoutEdges.NakedEdges);
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

    [Fact]
    public void Build_ZOnlyChangeWithSlopePeeling_RebuildsCulledTopology()
    {
        var engine = new TinEngine();
        double[] xy =
        {
            0.0, 0.0,
            1.0, 0.0,
            0.0, 1.0,
            100.0, 0.0001
        };
        double[] flatZ = { 0.0, 0.0, 0.0, 0.0 };
        double[] steepZ = { 0.0, 0.0, 0.0, 100.0 };
        var peelSettings = new BoundaryTrianglePeelSettings
        {
            MaxBoundaryEdgeLength = 1_000.0,
            MaxInteriorAngleDegrees = 180.0,
            MaxSlopeAngleDegrees = 45.0
        };

        TinResult? first = engine.Build(
            xy,
            flatZ,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? firstError,
            useConvexHull: true,
            boundaryPeelSettings: peelSettings);
        TinResult? second = engine.Build(
            xy,
            steepZ,
            Array.Empty<int>(),
            QualitySettings.None,
            out string? secondError,
            useConvexHull: true,
            boundaryPeelSettings: peelSettings);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, first!.FaceCount);
        Assert.Equal(1, second!.FaceCount);
    }

    private static (double[] Xy, double[] Z) BuildJitteredGrid(int rows, int cols)
    {
        int count = rows * cols;
        var xy = new double[count * 2];
        var z = new double[count];
        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            xy[i * 2] = col + 0.011 * i;
            xy[i * 2 + 1] = row + 0.013 * i;
            z[i] = i * 10.0;
        }
        return (xy, z);
    }

    private static (double[] Xy, double[] Z) RemovePoint((double[] Xy, double[] Z) input, int index)
    {
        int count = input.Z.Length;
        var xy = new double[(count - 1) * 2];
        var z = new double[count - 1];
        int w = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == index) continue;
            xy[w * 2] = input.Xy[i * 2];
            xy[w * 2 + 1] = input.Xy[i * 2 + 1];
            z[w] = input.Z[i];
            w++;
        }
        return (xy, z);
    }

    private static (double[] Xy, double[] Z) InsertPoint((double[] Xy, double[] Z) input, int index, double x, double y, double zVal)
    {
        int count = input.Z.Length;
        var xy = new double[(count + 1) * 2];
        var z = new double[count + 1];
        int w = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == index)
            {
                xy[w * 2] = x;
                xy[w * 2 + 1] = y;
                z[w] = zVal;
                w++;
            }
            xy[w * 2] = input.Xy[i * 2];
            xy[w * 2 + 1] = input.Xy[i * 2 + 1];
            z[w] = input.Z[i];
            w++;
        }
        if (index >= count)
        {
            xy[w * 2] = x;
            xy[w * 2 + 1] = y;
            z[w] = zVal;
        }
        return (xy, z);
    }

    private static void AssertVertexZMatchesInputByXy(TinResult result, double[] inputXy, double[] inputZ)
    {
        const double tol = 1e-6;
        int inputCount = inputZ.Length;
        for (int outIndex = 0; outIndex < result.VertexCount; outIndex++)
        {
            double x = result.Vertices[outIndex * 3];
            double y = result.Vertices[outIndex * 3 + 1];
            double z = result.Vertices[outIndex * 3 + 2];

            for (int i = 0; i < inputCount; i++)
            {
                double dx = inputXy[i * 2] - x;
                double dy = inputXy[i * 2 + 1] - y;
                if (dx * dx + dy * dy < tol * tol)
                {
                    Assert.True(
                        Math.Abs(inputZ[i] - z) < 1e-6,
                        $"Vertex at ({x},{y}) expected Z={inputZ[i]} but got Z={z}.");
                }
            }
        }
    }

    [Fact]
    public void Build_RemoveMiddlePoint_IncrementalEditKeepsCorrectZ()
    {
        var engine = new TinEngine();
        var grid = BuildJitteredGrid(4, 4);

        TinResult? first = engine.Build(grid.Xy, grid.Z, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true);
        Assert.Null(firstError);
        Assert.NotNull(first);

        var edited = RemovePoint(grid, index: 5);
        TinResult? second = engine.Build(edited.Xy, edited.Z, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true);

        Assert.Null(secondError);
        Assert.NotNull(second);
        AssertVertexZMatchesInputByXy(second!, edited.Xy, edited.Z);
    }

    [Fact]
    public void Build_InsertMiddlePoint_IncrementalEditKeepsCorrectZ()
    {
        var engine = new TinEngine();
        var grid = BuildJitteredGrid(4, 4);
        var reduced = RemovePoint(grid, index: 5);

        TinResult? first = engine.Build(reduced.Xy, reduced.Z, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true);
        Assert.Null(firstError);
        Assert.NotNull(first);

        var edited = InsertPoint(reduced, index: 5, x: 1.5, y: 1.4, zVal: 999.0);
        TinResult? second = engine.Build(edited.Xy, edited.Z, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true);

        Assert.Null(secondError);
        Assert.NotNull(second);
        AssertVertexZMatchesInputByXy(second!, edited.Xy, edited.Z);
    }

    [Fact]
    public void Build_ZOnlyChangeAfterIncrementalEdit_UpdatesCorrectVertices()
    {
        var engine = new TinEngine();
        var grid = BuildJitteredGrid(4, 4);

        TinResult? first = engine.Build(grid.Xy, grid.Z, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true);
        Assert.Null(firstError);
        Assert.NotNull(first);

        var edited = RemovePoint(grid, index: 5);
        TinResult? second = engine.Build(edited.Xy, edited.Z, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true);
        Assert.Null(secondError);
        Assert.NotNull(second);

        var zChanged = (double[])edited.Z.Clone();
        zChanged[0] = 12345.0;
        TinResult? third = engine.Build(edited.Xy, zChanged, Array.Empty<int>(), QualitySettings.None, out string? thirdError, useConvexHull: true);

        Assert.Null(thirdError);
        Assert.NotNull(third);
        AssertVertexZMatchesInputByXy(third!, edited.Xy, zChanged);
    }

    [Fact]
    public void Build_TwoSequentialMiddleRemovals_IncrementalEditsKeepCorrectZ()
    {
        var engine = new TinEngine();
        var grid = BuildJitteredGrid(5, 5);

        TinResult? first = engine.Build(grid.Xy, grid.Z, Array.Empty<int>(), QualitySettings.None, out string? firstError, useConvexHull: true);
        Assert.Null(firstError);
        Assert.NotNull(first);

        var firstEdit = RemovePoint(grid, index: 7);
        TinResult? second = engine.Build(firstEdit.Xy, firstEdit.Z, Array.Empty<int>(), QualitySettings.None, out string? secondError, useConvexHull: true);
        Assert.Null(secondError);
        Assert.NotNull(second);

        var secondEdit = RemovePoint(firstEdit, index: 12);
        TinResult? third = engine.Build(secondEdit.Xy, secondEdit.Z, Array.Empty<int>(), QualitySettings.None, out string? thirdError, useConvexHull: true);

        Assert.Null(thirdError);
        Assert.NotNull(third);
        AssertVertexZMatchesInputByXy(third!, secondEdit.Xy, secondEdit.Z);
    }
}
