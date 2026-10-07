using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A single degenerate terrain face must not fail the whole split. Real terrain out of a GIS remesh
/// carries slivers narrower than the merge tolerance; before these guards one of them collapsed the
/// per-face point set below three points, and Triangle.NET answered with 0 triangles on every
/// constrained tier plus a NullReferenceException in the plain-Delaunay fallback — taking every zone
/// in the terrain down with it.
/// </summary>
public class MeshAreaTopologySplitterDegenerateFaceTests
{
    private static MeshAreaSplitter.AreaBoundary Square(double x0, double y0, double x1, double y1)
    {
        var xy = new[] { x0, y0, x1, y0, x1, y1, x0, y1 };
        return new MeshAreaSplitter.AreaBoundary(xy, 4);
    }

    [Fact]
    public void TriangulationHelper_FewerThanThreeVertices_ReportsCauseInsteadOfNullReference()
    {
        var xy = new List<double> { 0, 0, 1, 0 };
        var outcome = TriangulationHelper.Triangulate(
            xy, 2, new List<(int a, int b)>(), 0, 0, convex: true, segmentSplitting: 0);

        Assert.Null(outcome.Mesh);
        Assert.NotNull(outcome.WarningMessage);
        Assert.Contains("at least 3", outcome.WarningMessage!);
        Assert.DoesNotContain("NullReference", outcome.WarningMessage!);
    }

    [Theory]
    // Exactly collinear corners: a real triangle count, but zero area.
    [InlineData(19.998, 50.0, 20.002, 50.0, 20.0, 50.003, "sub-tolerance on the boundary edge")]
    // Corners closer together than the merge tolerance: they collapse to one local point.
    [InlineData(19.999, 60.0, 20.001, 60.0, 20.0, 60.002, "sub-tolerance straddling the seam")]
    public void SplitPreservingTopology_DegenerateFace_StillSplitsTheRestOfTheMesh(
        double ax, double ay, double bx, double by, double cx, double cy, string shape)
    {
        const double tolerance = 0.005;

        // Two healthy triangles spanning the boundary, plus one degenerate face.
        var verts = new List<double>
        {
            0, 0, 0,
            100, 0, 0,
            100, 100, 0,
            0, 100, 0,
            ax, ay, 0,
            bx, by, 0,
            cx, cy, 0
        };
        var faces = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6 };

        var result = MeshAreaSplitter.SplitPreservingTopology(
            new IndexedTriMesh(verts.ToArray(), verts.Count / 3, faces, faces.Length / 3),
            new[] { Square(20, 20, 80, 80) }, tolerance, out string? warning);

        Assert.True(result != null, $"{shape} face failed the whole split: {warning}");
        Assert.Contains(result!.FaceAreaIndex, index => index == 0);   // something inside the zone
        Assert.Contains(result.FaceAreaIndex, index => index == -1);   // something outside it
        Assert.True(
            string.IsNullOrWhiteSpace(warning) || !warning!.Contains("NullReference"),
            $"unexpected warning for {shape}: {warning}");
    }

    [Fact]
    public void SplitPreservingTopology_FaceThatCannotBeRetriangulated_DegradesThatFaceOnly()
    {
        const double tolerance = 0.005;

        // A boundary whose vertices are packed far tighter than the merge tolerance: the cut points it
        // drops into a face collapse onto each other, so that face cannot be re-triangulated against
        // its constraints. The rest of the mesh must still be split.
        var dense = new List<double>();
        for (int i = 0; i < 400; i++)
        {
            double t = i / 400.0 * Math.PI * 2.0;
            // radius wobbles by far less than the merge tolerance
            double r = 40.0 + ((i % 2 == 0) ? 0.0 : 0.0004);
            dense.Add(50.0 + r * Math.Cos(t));
            dense.Add(50.0 + r * Math.Sin(t));
        }

        var verts = new double[] { 0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0 };
        var faces = new[] { 0, 1, 2, 0, 2, 3 };

        var result = MeshAreaSplitter.SplitPreservingTopology(
            new IndexedTriMesh(verts, 4, faces, 2),
            new[] { new MeshAreaSplitter.AreaBoundary(dense.ToArray(), dense.Count / 2) },
            tolerance, out string? warning);

        // The split must survive: a face it cannot subdivide is kept, never a null result.
        Assert.True(result != null, $"split collapsed entirely: {warning}");
        Assert.True(result!.FaceCount >= 2, "no faces were emitted");
    }

    [Fact]
    public void SplitPreservingTopology_FaceFailsRetriangulation_ReportsDegradedFaces()
    {
        const double tolerance = 0.005;
        var verts = new double[] { 0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0 };
        var faces = new[] { 0, 1, 2, 0, 2, 3 };

        MeshAreaSplitter.SplitResult? result;
        string? warning;
        MeshAreaTopologySplitter.ForceRetriangulationFailureForTesting = faceIndex => faceIndex == 0;
        try
        {
            result = MeshAreaSplitter.SplitPreservingTopology(
                new IndexedTriMesh(verts, 4, faces, 2), new[] { Square(20, 20, 80, 80) }, tolerance, out warning);
        }
        finally
        {
            MeshAreaTopologySplitter.ForceRetriangulationFailureForTesting = null;
        }

        // The degraded-face warning used to be overwritten by classification's own (null) message.
        Assert.NotNull(result);
        Assert.NotNull(warning);
        Assert.Contains("1 of 2 terrain faces", warning!);
        Assert.Contains("failure forced for testing", warning);
    }

    [Fact]
    public void SplitPreservingTopology_FacesFailRetriangulation_OutputStaysWatertight()
    {
        const double tolerance = 0.005;
        const int n = 5;
        const double spacing = 10.0;
        double extent = (n - 1) * spacing;
        var verts = new List<double>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                verts.AddRange(new[] { x * spacing, y * spacing, x + (0.5 * y) });

        var faces = new List<int>();
        for (int y = 0; y < n - 1; y++)
        {
            for (int x = 0; x < n - 1; x++)
            {
                int v00 = (y * n) + x;
                faces.AddRange(new[] { v00, v00 + 1, v00 + n + 1, v00, v00 + n + 1, v00 + n });
            }
        }

        MeshAreaSplitter.SplitResult? result;
        string? warning;
        // Every other face fails, so failed faces sit next to faces that did split their shared edges.
        MeshAreaTopologySplitter.ForceRetriangulationFailureForTesting = faceIndex => faceIndex % 2 == 0;
        try
        {
            result = MeshAreaSplitter.SplitPreservingTopology(
                new IndexedTriMesh(verts.ToArray(), verts.Count / 3, faces.ToArray(), faces.Count / 3),
                new[] { Square(15, 15, 25, 25) }, tolerance, out warning);
        }
        finally
        {
            MeshAreaTopologySplitter.ForceRetriangulationFailureForTesting = null;
        }

        Assert.NotNull(result);
        Assert.Contains("terrain faces", warning ?? string.Empty);

        var edgeUse = new Dictionary<(int, int), int>();
        for (int f = 0; f < result!.FaceCount; f++)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = result.Faces[(f * 3) + e];
                int b = result.Faces[(f * 3) + ((e + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }

        bool OnOuterBoundary(int vertex)
        {
            double x = result.Vertices[vertex * 3];
            double y = result.Vertices[(vertex * 3) + 1];
            return x <= 1e-9 || y <= 1e-9 || x >= extent - 1e-9 || y >= extent - 1e-9;
        }

        foreach (var (edge, count) in edgeUse)
        {
            Assert.True(count <= 2, $"edge {edge} is used {count} times");
            if (count == 1)
            {
                // A single-use edge inside the terrain is a T-junction or a crack.
                bool sameSide =
                    OnOuterBoundary(edge.Item1) && OnOuterBoundary(edge.Item2) &&
                    (Math.Abs(result.Vertices[edge.Item1 * 3] - result.Vertices[edge.Item2 * 3]) <= 1e-9 ||
                     Math.Abs(result.Vertices[(edge.Item1 * 3) + 1] - result.Vertices[(edge.Item2 * 3) + 1]) <= 1e-9);
                Assert.True(sameSide, $"interior edge {edge} is used only once");
            }
        }

        // The failed faces still contribute sub-triangles on both sides of the zone boundary.
        Assert.Contains(result.FaceAreaIndex, index => index == 0);
        Assert.Contains(result.FaceAreaIndex, index => index == -1);
    }
}
