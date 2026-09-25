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
            verts.ToArray(), verts.Count / 3, faces, faces.Length / 3,
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
            verts, 4, faces, 2,
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
                verts, 4, faces, 2, new[] { Square(20, 20, 80, 80) }, tolerance, out warning);
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
}
