using MoleHill.Core.Engine;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildServiceTinyFaceCleanupTests
{
    [Fact]
    public void ComputeTinyFaceCleanup_WhenBoundarySafeAndInteriorUnsafe_RemovesOnlySafeTinyFace()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            0.01, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 4.0, 0.0,
            0.0, 4.0, 0.0,
            1.99, 2.0, 0.0,
            2.01, 2.0, 0.0,
            2.0, 2.01, 0.0
        };
        int[] faces =
        {
            0, 1, 5,
            1, 2, 5,
            2, 6, 5,
            2, 3, 6,
            3, 7, 6,
            3, 4, 7,
            4, 5, 7,
            4, 0, 5,
            5, 6, 7
        };

        TerrainBuildService.TinyFaceCleanupResult cleanup = TerrainBuildService.ComputeTinyFaceCleanup(
            vertices,
            faces,
            faceCount: faces.Length / 3,
            tolerance: 0.05);

        Assert.Equal(1, cleanup.RemovedFaceCount);
        Assert.Equal(1, cleanup.BlockedFaceCount);
        Assert.False(ContainsFace(cleanup.Faces, 0, 1, 5));
        Assert.True(ContainsFace(cleanup.Faces, 5, 6, 7));

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(cleanup.Faces, cleanup.Faces.Length / 3);
        Assert.True(topology.HasSingleClosedBoundaryLoop);
    }

    [Fact]
    public void ComputeTinyFaceCleanup_WhenOnlyTinyFaceWouldCreateHole_KeepsOriginalFaces()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 4.0, 0.0,
            0.0, 4.0, 0.0,
            1.99, 2.0, 0.0,
            2.01, 2.0, 0.0,
            2.0, 2.01, 0.0
        };
        int[] faces =
        {
            0, 1, 4,
            1, 5, 4,
            1, 2, 5,
            2, 6, 5,
            2, 3, 6,
            3, 4, 6,
            3, 0, 4,
            4, 5, 6
        };

        TerrainBuildService.TinyFaceCleanupResult cleanup = TerrainBuildService.ComputeTinyFaceCleanup(
            vertices,
            faces,
            faceCount: faces.Length / 3,
            tolerance: 0.05);

        Assert.Equal(0, cleanup.RemovedFaceCount);
        Assert.Equal(1, cleanup.BlockedFaceCount);
        Assert.Equal(faces, cleanup.Faces);
    }

    [Fact]
    public void ComputeTinyFaceCleanup_WhenBorderFaceIsSliver_RemovesNearColinearTriangle()
    {
        TerrainBuildService.TinyFaceCleanupResult cleanup = TerrainBuildService.ComputeTinyFaceCleanup(
            BorderSliverVertices(sliverApexZ: 0.0),
            BorderSliverFaces,
            faceCount: BorderSliverFaces.Length / 3,
            tolerance: 0.01);

        Assert.Equal(1, cleanup.RemovedFaceCount);
        Assert.False(ContainsFace(cleanup.Faces, 0, 1, 4));
    }

    /// <summary>The same border face rising a metre is a wall face, thin only in plan: it stays.</summary>
    [Fact]
    public void ComputeTinyFaceCleanup_WhenBorderFaceIsSteep_KeepsIt()
    {
        TerrainBuildService.TinyFaceCleanupResult cleanup = TerrainBuildService.ComputeTinyFaceCleanup(
            BorderSliverVertices(sliverApexZ: 1.0),
            BorderSliverFaces,
            faceCount: BorderSliverFaces.Length / 3,
            tolerance: 0.01);

        Assert.Equal(0, cleanup.RemovedFaceCount);
        Assert.True(ContainsFace(cleanup.Faces, 0, 1, 4));
    }

    private static readonly int[] BorderSliverFaces =
    {
        0, 1, 4,
        1, 2, 5,
        2, 6, 5,
        2, 3, 6,
        3, 4, 6,
        3, 0, 4,
        4, 5, 6
    };

    private static double[] BorderSliverVertices(double sliverApexZ) => new[]
    {
        0.0, 0.0, 0.0,
        4.0, 0.0, 0.0,
        4.0, 4.0, 0.0,
        0.0, 4.0, 0.0,
        2.0, 0.001, sliverApexZ,
        2.01, 2.0, 0.0,
        2.0, 2.01, 0.0
    };

    /// <summary>
    /// A vertical wall has no plan area, so it reads as tiny in plan; a terrain with a hole (a Hide boundary) is
    /// not one closed border loop. The cleanup's old guard only engaged on one loop, so it deleted every wall face
    /// and tore the wall open, as the In-Situ Stair did on a copied case (2026-10-26).
    /// </summary>
    [Fact]
    public void ComputeTinyFaceCleanup_VerticalWallOnTerrainWithAHole_KeepsTheWall()
    {
        double[] vertices =
        {
            0.0, 0.0, 1.0, // 0 L0
            0.0, 6.0, 1.0, // 1 L1
            2.0, 0.0, 1.0, // 2 T0 wall top
            2.0, 3.0, 1.0, // 3 T1
            2.0, 6.0, 1.0, // 4 T2
            2.0, 0.0, 0.0, // 5 B0 wall toe, under the top
            2.0, 3.0, 0.0, // 6 B1
            2.0, 6.0, 0.0, // 7 B2
            6.0, 0.0, 0.0, // 8 R0
            6.0, 6.0, 0.0, // 9 R1
            4.0, 2.0, 0.0, // 10 H0 hole
            5.0, 2.0, 0.0, // 11 H1
            5.0, 4.0, 0.0, // 12 H2
            4.0, 4.0, 0.0  // 13 H3
        };
        int[] wall = { 2, 6, 3, 2, 5, 6, 3, 6, 7, 3, 7, 4 };
        int[] faces =
        {
            0, 2, 3, 0, 3, 1, 1, 3, 4,
            2, 6, 3, 2, 5, 6, 3, 6, 7, 3, 7, 4,
            5, 8, 10, 8, 11, 10, 8, 9, 11, 11, 9, 12, 12, 9, 7, 12, 7, 13, 13, 7, 6, 13, 6, 10, 10, 6, 5
        };

        TerrainBuildService.TinyFaceCleanupResult cleanup = TerrainBuildService.ComputeTinyFaceCleanup(
            vertices,
            faces,
            faceCount: faces.Length / 3,
            tolerance: 0.01);

        for (int i = 0; i < wall.Length; i += 3)
            Assert.True(ContainsFace(cleanup.Faces, wall[i], wall[i + 1], wall[i + 2]), $"Wall face {i / 3} was deleted.");
        Assert.Equal(0, cleanup.RemovedFaceCount);
    }

    private static bool ContainsFace(int[] faces, int a, int b, int c)
    {
        SortFace(ref a, ref b, ref c);
        for (int i = 0; i < faces.Length / 3; i++)
        {
            int fa = faces[i * 3];
            int fb = faces[i * 3 + 1];
            int fc = faces[i * 3 + 2];
            SortFace(ref fa, ref fb, ref fc);
            if (fa == a && fb == b && fc == c)
                return true;
        }

        return false;
    }

    private static void SortFace(ref int a, ref int b, ref int c)
    {
        if (a > b)
            (a, b) = (b, a);
        if (b > c)
            (b, c) = (c, b);
        if (a > b)
            (a, b) = (b, a);
    }
}
