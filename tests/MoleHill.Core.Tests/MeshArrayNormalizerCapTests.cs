using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Smooth on the Verandi Lendi terrain (2026-10-06): Remesh left a cap - three collinear corners, one on the
/// edge between the other two - and once smoothing made it exactly collinear, normalization culled it. The
/// cap's short edges and the long edge across from it were then each used once: a 0.3 m slit in the terrain.
/// </summary>
public sealed class MeshArrayNormalizerCapTests
{
    // Square 0-1-2-3 with r (4) on the diagonal 0-2. The cap (0, 4, 2) lies along the diagonal; (0, 1, 4)
    // and (4, 1, 2) sit on one side of it and (0, 2, 3) across it.
    private static readonly double[] Square = [0, 0, 0, 2, 0, 0, 2, 2, 1, 0, 2, 1, 1, 1, 0.5];
    private static readonly int[] WithInteriorCap = [0, 1, 4, 4, 1, 2, 0, 4, 2, 0, 2, 3];

    [Fact]
    public void SplitCollinearCaps_InteriorCap_SplitsTheFaceAcrossItsLongEdge()
    {
        int[] faces = MeshArrayNormalizer.SplitCollinearCaps(Square, WithInteriorCap, 4, out int faceCount, out int resolved);

        Assert.Equal(1, resolved);
        Assert.Equal(4, faceCount);
        AssertClosedExceptSquareBorder(faces, faceCount);
        Assert.Equal(4.0, PlanArea(Square, faces, faceCount), 12);
        for (int t = 0; t < faceCount; t++)
            Assert.True(SignedPlanArea(Square, faces, t) > 0, "Winding must be kept.");
    }

    [Fact]
    public void SplitCollinearCaps_BorderCap_IsDroppedWithoutASlit()
    {
        // Only the faces on one side of the diagonal: the cap's long edge is now the border.
        int[] faces = [0, 1, 4, 4, 1, 2, 0, 4, 2];
        int[] result = MeshArrayNormalizer.SplitCollinearCaps(Square, faces, 3, out int faceCount, out int resolved);

        Assert.Equal(1, resolved);
        Assert.Equal(new[] { 0, 1, 4, 4, 1, 2 }, result);
        Assert.Equal(2, faceCount);
    }

    [Fact]
    public void SplitCollinearCaps_NoCaps_ReturnsTheSameArray()
    {
        int[] faces = [0, 1, 2, 0, 2, 3];
        Assert.Same(faces, MeshArrayNormalizer.SplitCollinearCaps(Square, faces, 2, out int faceCount, out int resolved));
        Assert.Equal(2, faceCount);
        Assert.Equal(0, resolved);
    }

    [Fact]
    public void SplitCollinearCaps_ThenNormalize_KeepsTheTerrainClosed()
    {
        int[] faces = MeshArrayNormalizer.SplitCollinearCaps(Square, WithInteriorCap, 4, out int faceCount, out _);
        Assert.True(MeshArrayNormalizer.TryNormalize(Square, 5, faces, faceCount,
            out double[] vertices, out int _, out int[] normalized, out int normalizedCount));
        AssertClosedExceptSquareBorder(normalized, normalizedCount);
        Assert.Equal(4.0, PlanArea(vertices, normalized, normalizedCount), 6);
    }

    private static void AssertClosedExceptSquareBorder(int[] faces, int faceCount)
    {
        var uses = IndexedMeshTools.CreateEdgeKeyMap<int>(faceCount * 3);
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                long key = IndexedMeshTools.GetEdgeKey(faces[t * 3 + k], faces[t * 3 + ((k + 1) % 3)]);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        Assert.DoesNotContain(uses.Values, count => count > 2);
        Assert.Equal(4, uses.Values.Count(count => count == 1)); // the square's four sides, nothing else
    }

    private static double PlanArea(double[] v, int[] faces, int faceCount) =>
        Enumerable.Range(0, faceCount).Sum(t => Math.Abs(SignedPlanArea(v, faces, t)));

    private static double SignedPlanArea(double[] v, int[] f, int t)
    {
        int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
        return (((v[b * 3] - v[a * 3]) * (v[c * 3 + 1] - v[a * 3 + 1])) - ((v[b * 3 + 1] - v[a * 3 + 1]) * (v[c * 3] - v[a * 3]))) * 0.5;
    }
}
