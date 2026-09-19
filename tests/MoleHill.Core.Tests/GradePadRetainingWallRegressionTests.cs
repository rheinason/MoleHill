using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class GradePadRetainingWallRegressionTests
{
    // A pad whose daylight loop reaches the terrain edge pushes the terrain split onto the
    // full-terrain CDT re-conform, which re-triangulates EVERY terrain point - including a retaining
    // wall at the far side of the site that the pad never touches. Unless the wall breaklines go in
    // as exact constraints, the CDT flips the near-vertical wall-face edges away and the wall's foot
    // vertices stop bounding the terrain. Grade Path has always threaded its hard constraints here;
    // Grade Pad did not, which is what a distant pad wrecking a wall foot looked like.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SplitConformViaCdt_WithWallConstraints_KeepsTheWallFaceEdges(bool withConstraints)
    {
        const double footX = 10.0;
        const double topX = 10.2;

        var points = new List<double>();
        var segments = new List<(int a, int b)>();
        for (int ix = 0; ix <= 3; ix++)
        {
            for (int iy = 0; iy <= 3; iy++)
            {
                points.Add(ix * 10.0);
                points.Add(iy * 10.0);
            }
        }

        // The top rail is stationed independently of the foot rail, as a drawn wall always is: its
        // vertices interleave with the foot's, so an unconstrained Delaunay prefers the short
        // cross-strip edges over the long rails.
        int topStart = points.Count / 2;
        for (int iy = 0; iy < 3; iy++)
        {
            points.Add(topX);
            points.Add(5.0 + iy * 10.0);
            if (iy > 0)
                segments.Add((topStart + iy - 1, topStart + iy));
        }

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            points, points.Count / 2, segments, maxArea: 0.0, minAngle: 0.0, convex: false, segmentSplitting: 0);
        Assert.NotNull(outcome.Mesh);
        TriangleNetExtractor.Result terrain = TriangleNetExtractor.Extract(outcome.Mesh!);

        var vertices = new double[terrain.VertexCount * 3];
        for (int i = 0; i < terrain.VertexCount; i++)
        {
            double x = terrain.Xy[i * 2];
            vertices[i * 3] = x;
            vertices[i * 3 + 1] = terrain.Xy[i * 2 + 1];
            vertices[i * 3 + 2] = x > footX + 0.1 ? 1.5 : 0.0;
        }

        var wall = new[]
        {
            new SurfaceRemesher.ConstraintPolyline(BuildRail(footX, 0.0), 4, false, true),
            new SurfaceRemesher.ConstraintPolyline(BuildTopRail(topX, 1.5), 3, false, true),
        };

        // The pad's daylight loop, clipped to the terrain outline - the far third of the site.
        var loop = new[] { 20.0, 0.0, 30.0, 0.0, 30.0, 30.0, 20.0, 30.0 };
        double[]? outline = GradedRegionAssembler.TryBuildTerrainOutline(terrain.Faces, terrain.FaceCount, vertices);
        Assert.NotNull(outline);

        MeshAreaSplitter.SplitResult? conformed = GradedRegionAssembler.SplitConformViaCdt(
            vertices,
            terrain.VertexCount,
            terrain.Faces,
            terrain.FaceCount,
            new[] { loop },
            outline,
            tolerance: 0.001,
            hardConstraints: withConstraints ? wall : null);

        Assert.NotNull(conformed);

        bool footKept = HasEdge(conformed!, footX, 0.0, footX, 10.0) && HasEdge(conformed, footX, 10.0, footX, 20.0);
        bool topKept = HasEdge(conformed, topX, 5.0, topX, 15.0) && HasEdge(conformed, topX, 15.0, topX, 25.0);

        if (withConstraints)
        {
            Assert.True(footKept, "The wall foot breakline was not preserved through the CDT re-conform.");
            Assert.True(topKept, "The wall top breakline was not preserved through the CDT re-conform.");
        }
        else
        {
            // Documents the mechanism: with no constraints the re-conform is free to flip the wall
            // face away, which is exactly what the distant pad did to the wall foot.
            Assert.False(footKept && topKept, "Expected the unconstrained re-conform to lose a wall edge.");
        }
    }

    private static double[] BuildRail(double x, double z)
    {
        var points = new double[4 * 3];
        for (int iy = 0; iy <= 3; iy++)
        {
            points[iy * 3] = x;
            points[iy * 3 + 1] = iy * 10.0;
            points[iy * 3 + 2] = z;
        }

        return points;
    }

    private static double[] BuildTopRail(double x, double z)
    {
        var points = new double[3 * 3];
        for (int iy = 0; iy < 3; iy++)
        {
            points[iy * 3] = x;
            points[iy * 3 + 1] = 5.0 + iy * 10.0;
            points[iy * 3 + 2] = z;
        }

        return points;
    }

    private static bool HasEdge(MeshAreaSplitter.SplitResult mesh, double ax, double ay, double bx, double by)
    {
        int a = FindVertex(mesh, ax, ay);
        int b = FindVertex(mesh, bx, by);
        if (a < 0 || b < 0)
            return false;

        for (int i = 0; i < mesh.FaceCount * 3; i += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                int p = mesh.Faces[i + e];
                int q = mesh.Faces[i + (e + 1) % 3];
                if ((p == a && q == b) || (p == b && q == a))
                    return true;
            }
        }

        return false;
    }

    private static int FindVertex(MeshAreaSplitter.SplitResult mesh, double x, double y)
    {
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            if (Math.Abs(mesh.Vertices[i * 3] - x) < 1e-6 && Math.Abs(mesh.Vertices[i * 3 + 1] - y) < 1e-6)
                return i;
        }

        return -1;
    }
}
