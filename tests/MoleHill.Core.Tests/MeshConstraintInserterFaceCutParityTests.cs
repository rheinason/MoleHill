using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The constraint inserter and the area splitter share one face-cutting kernel. These pin the three
/// repairs the splitter had and the inserter lacked while they were separate copies: a clockwise face keeps
/// its winding, two corners closer than tolerance stay two vertices, and a face with no triangulable area
/// does not sink the whole insertion.
/// </summary>
public class MeshConstraintInserterFaceCutParityTests
{
    [Fact]
    public void TryInsert_ClockwiseTerrain_KeepsEveryFaceClockwise()
    {
        double[] vertices = TestMeshes.GridVertices(4, 4, z: (x, y) => 0.1 * x);
        int[] faces = Reverse(TestMeshes.GridFaces(4, 4));
        var constraint = new ConstraintPolyline(new[] { 0.3, 0.2, 0.0, 2.7, 2.6, 0.0 }, 2, false);

        bool ok = MeshConstraintTopologyInserter.TryInsert(
            vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { constraint }, 1e-6,
            out double[] outVertices, out _, out int[] outFaces, out int outFaceCount, out string? error);

        Assert.True(ok, error);
        Assert.True(outFaceCount > faces.Length / 3, "the constraint should have split faces");
        for (int f = 0; f < outFaceCount; f++)
            Assert.True(SignedArea(outVertices, outFaces, f) < 0.0, $"face {f} was re-wound counter-clockwise");
    }

    [Fact]
    public void TryInsert_FaceWithTwoCornersCloserThanTolerance_KeepsBothVertices()
    {
        // Two triangles share an edge shorter than the tolerance. Merging its ends in the local
        // triangulation dropped the triangle on that edge and left a slit; with an isolated point to place,
        // the local-triangulation fallback is not available, so the whole insertion failed.
        const double tolerance = 1e-3;
        double[] vertices =
        {
            0.0, 0.0, 0.0,      // 0 A
            0.0005, 0.0, 0.0,   // 1 B, 0.5 mm from A
            0.25, 1.0, 1.0,     // 2 C
            0.25, -1.0, -1.0    // 3 D
        };
        int[] faces = { 0, 1, 2, 1, 0, 3 };
        var constraint = new ConstraintPolyline(new[] { -1.0, 0.5, 0.0, 2.0, 0.5, 0.0 }, 2, false);

        bool ok = MeshConstraintTopologyInserter.TryInsert(
            vertices, 4, faces, 2, new[] { constraint }, new[] { 0.1876, 0.75 }, tolerance,
            out double[] outVertices, out _, out int[] outFaces, out int outFaceCount,
            out MeshConstraintTopologyInserter.PointPlacement placement, out string? error);

        Assert.True(ok, error);
        Assert.Equal(1, placement.Inserted);
        Assert.Equal(2, CountEdgeUses(outFaces, outFaceCount, 0, 1));
        AssertTilesPlanArea(vertices, faces, 2, outVertices, outFaces, outFaceCount);
    }

    [Fact]
    public void TryInsert_ZeroAreaCapCrossedByConstraint_Succeeds()
    {
        // M lies exactly on A-B, so face A-B-M has no area: a cap, as float-rounded terrain carries by the
        // hundred. Its local point set is collinear; Triangle.NET cannot triangulate it.
        double[] vertices =
        {
            0.0, 0.0, 0.0,    // 0 A
            2.0, 0.0, 0.0,    // 1 B
            1.0, 0.0, 0.0,    // 2 M, on A-B
            1.0, 1.0, 1.0,    // 3 C
            1.0, -1.0, -1.0   // 4 D
        };
        int[] faces = { 0, 2, 3, 2, 1, 3, 0, 4, 1, 0, 1, 2 };
        var constraint = new ConstraintPolyline(new[] { 0.5, -0.8, 0.0, 0.5, 0.8, 0.0 }, 2, false);

        bool ok = MeshConstraintTopologyInserter.TryInsert(
            vertices, 5, faces, 4, new[] { constraint }, 1e-6,
            out _, out _, out _, out int outFaceCount, out string? error);

        Assert.True(ok, error);
        Assert.True(outFaceCount > 4);
    }

    private static int[] Reverse(int[] faces)
    {
        var reversed = (int[])faces.Clone();
        for (int f = 0; f < reversed.Length / 3; f++)
            (reversed[(f * 3) + 1], reversed[(f * 3) + 2]) = (reversed[(f * 3) + 2], reversed[(f * 3) + 1]);
        return reversed;
    }

    private static double SignedArea(double[] vertices, int[] faces, int f)
    {
        int a = faces[f * 3], b = faces[(f * 3) + 1], c = faces[(f * 3) + 2];
        double ax = vertices[a * 3], ay = vertices[(a * 3) + 1];
        return 0.5 * (((vertices[b * 3] - ax) * (vertices[(c * 3) + 1] - ay)) -
                      ((vertices[(b * 3) + 1] - ay) * (vertices[c * 3] - ax)));
    }

    /// <summary>How many faces use the edge between vertices <paramref name="u"/> and <paramref name="w"/>.</summary>
    private static int CountEdgeUses(int[] faces, int faceCount, int u, int w)
    {
        int count = 0;
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[(f * 3) + k], b = faces[(f * 3) + ((k + 1) % 3)];
                if ((a == u && b == w) || (a == w && b == u))
                    count++;
            }
        }

        return count;
    }

    private static void AssertTilesPlanArea(double[] inVertices, int[] inFaces, int inFaceCount, double[] outVertices, int[] outFaces, int outFaceCount)
    {
        double before = 0.0, after = 0.0;
        for (int f = 0; f < inFaceCount; f++)
            before += Math.Abs(SignedArea(inVertices, inFaces, f));
        for (int f = 0; f < outFaceCount; f++)
            after += Math.Abs(SignedArea(outVertices, outFaces, f));
        Assert.Equal(before, after, 9);
    }
}
