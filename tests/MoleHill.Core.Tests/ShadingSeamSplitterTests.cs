using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class ShadingSeamSplitterTests
{
    private static float[] UpNormals(int vertexCount)
    {
        var n = new float[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
            n[i * 3 + 2] = 1f;
        return n;
    }

    [Fact]
    public void Split_GentleTerrainWithoutWalls_ReportsNothingToDo()
    {
        double[] v = { 0, 0, 0, 1, 0, 0.1, 1, 1, 0.2, 0, 1, 0.1 };
        int[] f = { 0, 1, 2, 0, 2, 3 };

        Assert.Null(ShadingSeamSplitter.Split(v, 4, f, 2, UpNormals(4), 70, 70));
    }

    /// <summary>
    /// Ground at z 0 for x in [0, 1], a vertical wall face at x = 1 up to z 1, ground at z 1 beyond. The two
    /// rail lines (x = 1 at z 0 and z 1) are seams: their vertices split so the ground keeps an upward
    /// normal and the wall a horizontal one, instead of one averaged normal smeared across both.
    /// </summary>
    [Fact]
    public void Split_VerticalWallBetweenTwoLevels_GivesGroundAndWallTheirOwnNormals()
    {
        double[] v =
        {
            0, 0, 0,   0, 1, 0,      // 0, 1: lower ground
            1, 0, 0,   1, 1, 0,      // 2, 3: wall toe
            1, 0, 1,   1, 1, 1,      // 4, 5: wall top (same plan position, 1 m up)
            2, 0, 1,   2, 1, 1       // 6, 7: upper ground
        };
        int[] f =
        {
            0, 2, 3,   0, 3, 1,      // lower ground
            2, 4, 5,   2, 5, 3,      // wall (vertical)
            4, 6, 7,   4, 7, 5       // upper ground
        };

        ShadingSeamSplitter.Result? split = ShadingSeamSplitter.Split(v, 8, f, 6, UpNormals(8), 70, 70);

        Assert.NotNull(split);
        Assert.Equal(2, split!.SeamEdgeCount);                // the toe edge 2-3 and the top edge 4-5
        Assert.Equal(4, split.SourceVertexOfCopy.Length);     // each rail vertex gets one copy
        // Every wall face corner on a rail now uses a vertex whose normal is horizontal (along -x or +x).
        for (int t = 2; t < 4; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int vertex = split.Faces[t * 3 + k];
                Assert.True(Math.Abs(split.Normals[vertex * 3 + 2]) < 1e-6, $"wall corner {vertex} normal z {split.Normals[vertex * 3 + 2]}");
            }
        }

        // Every ground face corner on a rail keeps an upward normal.
        foreach (int t in new[] { 0, 1, 4, 5 })
        {
            for (int k = 0; k < 3; k++)
            {
                int vertex = split.Faces[t * 3 + k];
                Assert.True(split.Normals[vertex * 3 + 2] > 0.99f, $"ground corner {vertex} normal z {split.Normals[vertex * 3 + 2]}");
            }
        }
    }
}
