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
