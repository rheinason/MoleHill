using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The snapper may index only the terrain a constraint set can reach. These tests are geometry-equivalence
/// tests, not timing tests: a clipped index must answer every query exactly as the full index does, and a
/// query that escapes the declared region must fall back to the full index rather than silently miss.
/// </summary>
public class ConstraintCoincidenceSnapperRegionTests
{
    // A 20x20 grid of unit quads, split into triangles: 441 vertices, 800 faces, 1,240 unique edges.
    private const int Divisions = 20;

    private static void BuildGrid(out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        int side = Divisions + 1;
        vertexCount = side * side;
        vertices = new double[vertexCount * 3];
        for (int y = 0; y <= Divisions; y++)
        {
            for (int x = 0; x <= Divisions; x++)
            {
                int i = (y * side) + x;
                vertices[i * 3] = x;
                vertices[i * 3 + 1] = y;
                vertices[i * 3 + 2] = 0.0;
            }
        }

        faceCount = Divisions * Divisions * 2;
        faces = new int[faceCount * 3];
        int f = 0;
        for (int y = 0; y < Divisions; y++)
        {
            for (int x = 0; x < Divisions; x++)
            {
                int a = (y * side) + x;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;
                faces[f++] = a; faces[f++] = b; faces[f++] = d;
                faces[f++] = a; faces[f++] = d; faces[f++] = c;
            }
        }
    }

    private static ConstraintPolyline Polyline(params double[] xyz) =>
        new(xyz, xyz.Length / 3, IsClosed: false, PreserveInputElevation: false);

    [Fact]
    public void ForConstraints_ClippedIndex_SnapsIdenticallyToFullIndex()
    {
        BuildGrid(out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        const double tolerance = 0.25;

        // A short constraint in one corner of a terrain that extends 20 units away from it.
        var constraints = new List<ConstraintPolyline>
        {
            Polyline(
                2.1, 2.05, 3.0,
                3.9, 2.2, 3.0,
                5.05, 3.5, 3.0)
        };

        var full = new ConstraintCoincidenceSnapper(vertices, vertexCount, faces, faceCount, tolerance);
        var clipped = ConstraintCoincidenceSnapper.ForConstraints(
            vertices, vertexCount, faces, faceCount, tolerance, constraints);

        ConstraintPolyline fullResult = full.SnapConstraintPolyline(constraints[0]);
        ConstraintPolyline clippedResult = clipped.SnapConstraintPolyline(constraints[0]);

        Assert.False(clipped.RegionWasAbandoned);
        Assert.Equal(fullResult.PointCount, clippedResult.PointCount);
        Assert.Equal(fullResult.Points, clippedResult.Points);
    }

    [Fact]
    public void ForConstraints_LocalConstraint_IndexesFarLessThanTheWholeMesh()
    {
        BuildGrid(out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        const double tolerance = 0.25;

        var constraints = new List<ConstraintPolyline>
        {
            Polyline(2.1, 2.05, 3.0, 3.9, 2.2, 3.0)
        };

        var full = new ConstraintCoincidenceSnapper(vertices, vertexCount, faces, faceCount, tolerance);
        var clipped = ConstraintCoincidenceSnapper.ForConstraints(
            vertices, vertexCount, faces, faceCount, tolerance, constraints);

        Assert.Equal(vertexCount, full.IndexedCounts.Vertices);
        Assert.True(
            clipped.IndexedCounts.Edges * 10 < full.IndexedCounts.Edges,
            $"clipped index held {clipped.IndexedCounts.Edges} edges of {full.IndexedCounts.Edges}");
    }

    [Fact]
    public void SnapPoint_QueryOutsideTheDeclaredRegion_RebuildsOverTheWholeMesh()
    {
        BuildGrid(out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        const double tolerance = 0.25;

        var declared = new List<ConstraintPolyline>
        {
            Polyline(2.1, 2.05, 3.0, 3.9, 2.2, 3.0)
        };
        var clipped = ConstraintCoincidenceSnapper.ForConstraints(
            vertices, vertexCount, faces, faceCount, tolerance, declared);
        var full = new ConstraintCoincidenceSnapper(vertices, vertexCount, faces, faceCount, tolerance);

        // Snap a point the declared region never covered: it must still land on the far grid vertex.
        clipped.SnapPoint(16.05, 17.1, out double x, out double y);
        full.SnapPoint(16.05, 17.1, out double fullX, out double fullY);

        Assert.True(clipped.RegionWasAbandoned);
        Assert.Equal(fullX, x);
        Assert.Equal(fullY, y);
        Assert.Equal(16.0, x);
        Assert.Equal(17.0, y);
    }

    [Fact]
    public void RegionCovering_NoConstraintPoints_ReturnsNullSoTheWholeMeshIsIndexed()
    {
        Assert.Null(ConstraintCoincidenceSnapper.RegionCovering(
            new List<ConstraintPolyline>(), 0.1));
        Assert.Null(ConstraintCoincidenceSnapper.RegionCovering(
            new List<ConstraintPolyline> { Polyline() }, 0.1));
    }

    [Fact]
    public void RegionCovering_NaNPoint_ReturnsNullRatherThanAnUnusableRegion()
    {
        var constraints = new List<ConstraintPolyline>
        {
            Polyline(1.0, 1.0, 0.0, double.NaN, 2.0, 0.0)
        };

        Assert.Null(ConstraintCoincidenceSnapper.RegionCovering(constraints, 0.1));
    }

    [Fact]
    public void ForConstraints_ConstraintSpanningTheWholeMesh_MatchesTheFullIndexEverywhere()
    {
        BuildGrid(out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        const double tolerance = 0.3;

        var constraints = new List<ConstraintPolyline>
        {
            Polyline(0.1, 0.05, 1.0, 19.9, 19.95, 1.0),
            Polyline(0.2, 19.8, 1.0, 19.7, 0.15, 1.0)
        };

        var full = new ConstraintCoincidenceSnapper(vertices, vertexCount, faces, faceCount, tolerance);
        var clipped = ConstraintCoincidenceSnapper.ForConstraints(
            vertices, vertexCount, faces, faceCount, tolerance, constraints);

        foreach (ConstraintPolyline constraint in constraints)
        {
            ConstraintPolyline expected = full.SnapConstraintPolyline(constraint);
            ConstraintPolyline actual = clipped.SnapConstraintPolyline(constraint);
            Assert.Equal(expected.Points, actual.Points);
        }

        Assert.False(clipped.RegionWasAbandoned);
    }
}
