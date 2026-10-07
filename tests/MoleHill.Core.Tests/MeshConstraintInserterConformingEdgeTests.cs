using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Constraint insertion splits faces one at a time. When a constraint point lands on an edge shared by
/// two faces, BOTH incident faces must subdivide that edge identically — same split point, same
/// elevation — or the mesh is left non-conforming: the vertex sits in the interior of the neighbour's
/// edge, that edge is used by one face only, and every downstream boundary analysis reads it as a
/// naked edge.
///
/// This is not a hole. Total surface area is unchanged and every face is still there; the surface is
/// covered but the connectivity is wrong. It presents as phantom "extra boundary loops", which is how
/// it surfaced: a graded retaining wall reported 3 boundary loops on a terrain that had 1, the grading
/// tier refused the result, and the wall stage fell back to a rebuild that discarded terrain detail.
///
/// The two-triangle fixture is the whole defect in miniature: a constraint that ENDS on the shared
/// diagonal touches one face along its length and the other at a single point.
/// </summary>
public class MeshConstraintInserterConformingEdgeTests
{
    /// <summary>Unit square as two triangles sharing the diagonal 0-2; midpoint of that diagonal is (0.5, 0.5).</summary>
    private static (double[] vertices, int vertexCount, int[] faces, int faceCount) TwoTrianglesSharingDiagonal()
    {
        var vertices = new double[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0
        };
        var faces = new[] { 0, 1, 2, 0, 2, 3 };
        return (vertices, 4, faces, 2);
    }

    private static ConstraintPolyline Polyline(params double[] xyz) =>
        new(xyz, xyz.Length / 3, IsClosed: false, PreserveInputElevation: false);

    /// <summary>
    /// Counts vertices lying strictly inside a naked edge. A conforming mesh has none: an interior
    /// vertex on a once-used edge is precisely the non-conforming split this guards.
    /// </summary>
    private static int CountTJunctions(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        var useCount = new Dictionary<(int, int), int>();
        void Bump(int x, int y)
        {
            var key = x < y ? (x, y) : (y, x);
            useCount[key] = useCount.TryGetValue(key, out int c) ? c + 1 : 1;
        }

        for (int i = 0; i < faceCount; i++)
        {
            int a = faces[i * 3], b = faces[i * 3 + 1], c = faces[i * 3 + 2];
            Bump(a, b);
            Bump(b, c);
            Bump(c, a);
        }

        int tJunctions = 0;
        foreach (var (edge, count) in useCount)
        {
            if (count != 1)
                continue;

            double ax = vertices[edge.Item1 * 3], ay = vertices[edge.Item1 * 3 + 1];
            double bx = vertices[edge.Item2 * 3], by = vertices[edge.Item2 * 3 + 1];
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = (dx * dx) + (dy * dy);
            if (lengthSquared < 1e-18)
                continue;

            for (int v = 0; v < vertexCount; v++)
            {
                if (v == edge.Item1 || v == edge.Item2)
                    continue;

                double px = vertices[v * 3], py = vertices[v * 3 + 1];
                double t = (((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared;
                if (t <= 1e-9 || t >= 1.0 - 1e-9)
                    continue;

                double qx = ax + (dx * t), qy = ay + (dy * t);
                double distanceSquared = ((px - qx) * (px - qx)) + ((py - qy) * (py - qy));
                if (distanceSquared < 1e-14)
                {
                    tJunctions++;
                    break;
                }
            }
        }

        return tJunctions;
    }

    private static double ProjectedArea(double[] vertices, int[] faces, int faceCount)
    {
        double total = 0.0;
        for (int i = 0; i < faceCount; i++)
        {
            int a = faces[i * 3], b = faces[i * 3 + 1], c = faces[i * 3 + 2];
            double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1];
            double cx = vertices[c * 3], cy = vertices[c * 3 + 1];
            total += Math.Abs(((bx - ax) * (cy - ay)) - ((cx - ax) * (by - ay))) * 0.5;
        }

        return total;
    }

    [Theory]
    // A constraint that ends on the shared diagonal, entering from either face...
    [InlineData(0.75, 0.25, 0.5, 0.5, "ends on shared edge from face 0")]
    [InlineData(0.25, 0.75, 0.5, 0.5, "ends on shared edge from face 1")]
    // ...and the same constraint written backwards, so it BEGINS on the shared edge.
    [InlineData(0.5, 0.5, 0.75, 0.25, "starts on shared edge into face 0")]
    [InlineData(0.5, 0.5, 0.25, 0.75, "starts on shared edge into face 1")]
    public void Insert_ConstraintTerminatingOnSharedEdge_LeavesNoTJunction(
        double x0, double y0, double x1, double y1, string because)
    {
        var (vertices, vertexCount, faces, faceCount) = TwoTrianglesSharingDiagonal();
        var constraints = new[] { Polyline(x0, y0, 0.0, x1, y1, 0.0) };

        bool inserted = MeshConstraintTopologyInserter.TryInsert(
            new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            constraints,
            1e-6,
            out IndexedTriMesh insertedMesh,
            out string? error);
        double[] outVertices = insertedMesh.Vertices;
        int outVertexCount = insertedMesh.VertexCount;
        int[] outFaces = insertedMesh.Faces;
        int outFaceCount = insertedMesh.FaceCount;

        Assert.True(inserted, $"{because}: insertion failed: {error}");
        Assert.Equal(
            ProjectedArea(vertices, faces, faceCount),
            ProjectedArea(outVertices, outFaces, outFaceCount),
            precision: 9);
        Assert.Equal(0, CountTJunctions(outVertices, outVertexCount, outFaces, outFaceCount));
    }

    /// <summary>
    /// Face order must not decide the outcome. The same square with its two triangles listed in the
    /// opposite order has to subdivide the shared diagonal the same way.
    /// </summary>
    [Fact]
    public void Insert_ConstraintTerminatingOnSharedEdge_IsIndependentOfFaceOrder()
    {
        var (vertices, vertexCount, _, _) = TwoTrianglesSharingDiagonal();
        var constraints = new[] { Polyline(0.75, 0.25, 0.0, 0.5, 0.5, 0.0) };

        var forward = new[] { 0, 1, 2, 0, 2, 3 };
        var reversed = new[] { 0, 2, 3, 0, 1, 2 };

        foreach (var (faces, label) in new[] { (forward, "forward"), (reversed, "reversed") })
        {
            bool inserted = MeshConstraintTopologyInserter.TryInsert(
                new IndexedTriMesh(vertices, vertexCount, faces, 2),
                constraints,
                1e-6,
                out IndexedTriMesh insertedMesh2,
                out string? error);
            double[] outVertices = insertedMesh2.Vertices;
            int outVertexCount = insertedMesh2.VertexCount;
            int[] outFaces = insertedMesh2.Faces;
            int outFaceCount = insertedMesh2.FaceCount;

            Assert.True(inserted, $"{label}: {error}");
            Assert.Equal(0, CountTJunctions(outVertices, outVertexCount, outFaces, outFaceCount));
        }
    }

    /// <summary>
    /// A constraint crossing clean through both faces already worked; it is here so a fix to the
    /// terminating case cannot regress the ordinary one.
    /// </summary>
    [Fact]
    public void Insert_ConstraintCrossingBothFaces_LeavesNoTJunction()
    {
        var (vertices, vertexCount, faces, faceCount) = TwoTrianglesSharingDiagonal();
        var constraints = new[] { Polyline(0.9, 0.1, 0.0, 0.1, 0.9, 0.0) };

        bool inserted = MeshConstraintTopologyInserter.TryInsert(
            new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            constraints,
            1e-6,
            out IndexedTriMesh insertedMesh3,
            out string? error);
        double[] outVertices = insertedMesh3.Vertices;
        int outVertexCount = insertedMesh3.VertexCount;
        int[] outFaces = insertedMesh3.Faces;
        int outFaceCount = insertedMesh3.FaceCount;

        Assert.True(inserted, error);
        Assert.Equal(0, CountTJunctions(outVertices, outVertexCount, outFaces, outFaceCount));
    }
}
