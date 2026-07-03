using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

public class QuadRetopoCleanupTests
{
    [Fact]
    public void CloseInternalBoundaryLoops_FillsQuadLatticeHole()
    {
        (double[] vertices, int[] sourceFaces) = Grid(4);
        int[] quads = RingQuadsAroundCenterCell();

        int[] beforeFaces = Triangulate(quads, Array.Empty<int>());
        MeshTopologyValidator.BoundaryGraphAnalysis before =
            MeshTopologyValidator.AnalyzeBoundaryGraph(beforeFaces, beforeFaces.Length / 3);
        Assert.Equal(2, before.BoundaryComponentCount);

        QuadRetopoCleanup.CleanupResult cleanup = QuadRetopoCleanup.CloseInternalBoundaryLoops(
            vertices,
            sourceFaces,
            vertices,
            quads,
            Array.Empty<int>(),
            new QuadRetopoCleanup.Options { Tolerance = 0.001 });

        Assert.Equal(1, cleanup.ClosedLoopCount);
        Assert.True(cleanup.AddedTriangleCount > 0);

        int[] afterFaces = Triangulate(cleanup.Quads, cleanup.Tris);
        MeshTopologyValidator.BoundaryGraphAnalysis after =
            MeshTopologyValidator.AnalyzeBoundaryGraph(afterFaces, afterFaces.Length / 3);
        Assert.Equal(1, after.BoundaryComponentCount);
        Assert.False(after.HasOpenBoundaryChains);
        Assert.Equal(0, after.NonManifoldEdgeCount);
    }

    [Fact]
    public void WeldByTolerance_MergesDuplicateVerticesAndKeepsFaces()
    {
        var vertices = new double[]
        {
            0, 0, 0,
            1, 0, 0,
            1, 1, 0,
            0, 1, 0,
            0.0004, 0.0003, 0,
        };
        int[] tris = { 0, 1, 2, 4, 2, 3 };

        QuadRetopoCleanup.WeldResult welded = QuadRetopoCleanup.WeldByTolerance(
            vertices,
            Array.Empty<int>(),
            tris,
            tolerance: 0.001);

        Assert.Equal(1, welded.RemovedVertexCount);
        Assert.Equal(0, welded.DroppedFaceCount);
        Assert.Equal(4, welded.Vertices.Length / 3);
        Assert.Equal(2, welded.Tris.Length / 3);
        Assert.Contains(0, welded.Tris);
    }

    [Fact]
    public void CloseInternalBoundaryLoops_SkipsLargeHoleInsteadOfFanFilling()
    {
        (double[] vertices, int[] sourceFaces) = Grid(4);
        int[] quads = RingQuadsAroundCenterCell();

        QuadRetopoCleanup.CleanupResult cleanup = QuadRetopoCleanup.CloseInternalBoundaryLoops(
            vertices,
            sourceFaces,
            vertices,
            quads,
            Array.Empty<int>(),
            new QuadRetopoCleanup.Options { Tolerance = 0.001, MaxFanFillArea = 0.5 });

        Assert.Equal(0, cleanup.ClosedLoopCount);
        Assert.Equal(1, cleanup.SkippedLargeLoopCount);
        Assert.Empty(cleanup.Tris);

        int[] afterFaces = Triangulate(cleanup.Quads, cleanup.Tris);
        MeshTopologyValidator.BoundaryGraphAnalysis after =
            MeshTopologyValidator.AnalyzeBoundaryGraph(afterFaces, afterFaces.Length / 3);
        Assert.Equal(2, after.BoundaryComponentCount);
    }

    private static (double[] vertices, int[] faces) Grid(int n)
    {
        var vertices = new double[n * n * 3];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = (y * n) + x;
                vertices[i * 3] = x;
                vertices[i * 3 + 1] = y;
                vertices[i * 3 + 2] = x + y;
            }
        }

        var faces = new List<int>();
        for (int y = 0; y < n - 1; y++)
        {
            for (int x = 0; x < n - 1; x++)
            {
                int a = (y * n) + x;
                int b = (y * n) + x + 1;
                int c = ((y + 1) * n) + x + 1;
                int d = ((y + 1) * n) + x;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        return (vertices, faces.ToArray());
    }

    private static int[] RingQuadsAroundCenterCell()
    {
        var quads = new List<int>();
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                if (x == 1 && y == 1)
                    continue;

                int a = (y * 4) + x;
                int b = (y * 4) + x + 1;
                int c = ((y + 1) * 4) + x + 1;
                int d = ((y + 1) * 4) + x;
                quads.AddRange(new[] { a, b, c, d });
            }
        }

        return quads.ToArray();
    }

    private static int[] Triangulate(int[] quads, int[] tris)
    {
        var faces = new List<int>((quads.Length / 4 * 6) + tris.Length);
        for (int i = 0; i < quads.Length / 4; i++)
        {
            int a = quads[i * 4];
            int b = quads[i * 4 + 1];
            int c = quads[i * 4 + 2];
            int d = quads[i * 4 + 3];
            faces.AddRange(new[] { a, b, c, a, c, d });
        }

        faces.AddRange(tris);
        return faces.ToArray();
    }
}
