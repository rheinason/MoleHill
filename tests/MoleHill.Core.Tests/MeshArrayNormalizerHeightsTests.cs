using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A stage that moves a normalized mesh's vertices in height only (Smooth) skips normalizing again exactly when
/// normalizing would change nothing: no two vertices meet at one float position and no face goes collinear.
/// </summary>
public class MeshArrayNormalizerHeightsTests
{
    // Two triangles over a unit square, plus a vertical wall face: vertices 4 and 5 share vertex 1's plan
    // position at other heights.
    private static readonly int[] Faces = { 0, 1, 2, 0, 2, 3, 1, 4, 5 };

    private static double[] Vertices(double z4, double z5) =>
        new[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 0, z4, 2, 0, z5 };

    [Fact]
    public void HeightsKeepNormalForm_HeightsMovedApart_ReturnsTrueAndNormalizingChangesNothing()
    {
        double[] vertices = Vertices(1.0, 0.5);

        Assert.True(MeshArrayNormalizer.HeightsKeepNormalForm(vertices, 6, Faces, 3));
        Assert.True(MeshArrayNormalizer.TryNormalize(vertices, 6, Faces, 3, out double[] v, out int vc, out int[] f, out int fc));
        Assert.Equal(vertices, v[..(vc * 3)]);
        Assert.Equal(Faces, f[..(fc * 3)]);
    }

    [Fact]
    public void HeightsKeepNormalForm_TwoVerticesMeet_ReturnsFalse()
    {
        // Vertex 4 smoothed down onto vertex 1: a merge, which re-sorts every vertex.
        Assert.False(MeshArrayNormalizer.HeightsKeepNormalForm(Vertices(0.0, 0.5), 6, Faces, 3));
    }

    [Fact]
    public void HeightsKeepNormalForm_FaceGoesCollinear_ReturnsFalse()
    {
        // Face 1-4-5 with its three corners on one line, (1,0,0), (2,0,0), (3,0,0): a cull.
        double[] vertices = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 2, 0, 0, 3, 0, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3, 1, 4, 5 };

        Assert.False(MeshArrayNormalizer.HeightsKeepNormalForm(vertices, 6, faces, 3));
    }
}
