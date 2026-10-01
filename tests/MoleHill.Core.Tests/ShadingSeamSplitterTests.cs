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
        AssertGroundCornersUp(split, new[] { 0, 1, 4, 5 });
    }

    /// <summary>
    /// A seam vertex's ground group holds a tiny flat face and a large 40° face. Its normal is the mean of
    /// the two unit normals, as Rhino computes it, not an area-weighted mean that the large face would
    /// dominate: found live, where that tilted flat ground beside a wall by 30–37°.
    /// </summary>
    [Fact]
    public void Split_GroupNormal_IsTheUnweightedMeanOfItsFaces()
    {
        double[] v =
        {
            0, 0, 0,          // 0: seam vertex on the wall toe
            0, 1, 0,          // 1: toe
            0, 0, 1,          // 2: wall top
            0, 1, 1,          // 3: wall top
            -0.1, 0.5, 0,     // 4: tiny flat ground face
            -5, -5, 5         // 5: large 40° ground face
        };
        int[] f =
        {
            0, 1, 4,          // flat ground (area 0.05), first so the ground keeps vertex 0
            0, 4, 5,          // 40° ground (area ~2)
            0, 1, 3,          // wall
            0, 3, 2           // wall
        };

        ShadingSeamSplitter.Result? split = ShadingSeamSplitter.Split(v, 6, f, 4, UpNormals(6), 70, 70);

        Assert.NotNull(split);
        double ex = 2.5 / Math.Sqrt(15.5), ey = 0.5 / Math.Sqrt(15.5), ez = (3.0 / Math.Sqrt(15.5)) + 1.0;
        double el = Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
        Assert.Equal(ex / el, split!.Normals[0], 4);
        Assert.Equal(ey / el, split.Normals[1], 4);
        Assert.Equal(ez / el, split.Normals[2], 4);
    }

    private static void AssertGroundCornersUp(ShadingSeamSplitter.Result split, int[] groundFaces)
    {
        foreach (int t in groundFaces)
        {
            for (int k = 0; k < 3; k++)
            {
                int vertex = split.Faces[t * 3 + k];
                Assert.True(split.Normals[vertex * 3 + 2] > 0.99f, $"ground corner {vertex} normal z {split.Normals[vertex * 3 + 2]}");
            }
        }
    }
}
