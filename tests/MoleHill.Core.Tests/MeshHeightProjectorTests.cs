using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshHeightProjectorTests
{
    [Fact]
    public void TryProjectZ_InsideSingleFace_InterpolatesHeight()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            2.0, 0.0, 2.0,
            0.0, 2.0, 4.0
        };
        int[] faces = { 0, 1, 2 };
        var projector = new MeshHeightProjector(vertices, 3, faces, 1);

        bool projected = projector.TryProjectZ(
            0.5,
            0.5,
            sampleZ: 0.0,
            tolerance: 1e-8,
            out double z,
            out var status);

        Assert.True(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.Projected, status);
        Assert.Equal(1.5, z, precision: 12);
    }

    [Fact]
    public void TryGetHeightExtremes_LongSliver_HoldsTheToleranceAcrossItsLongEdge()
    {
        // A 2.8 m sliver 14 mm tall, its long edge on the diagonal so points just outside it stay in its
        // bounding box: the tolerance is a distance, so 0.5 mm out is in and 5 mm out is not.
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            2.0, 2.0, 0.0,
            0.99, 1.01, 0.0
        };
        var projector = new MeshHeightProjector(vertices, 3, new[] { 0, 1, 2 }, 1);
        static (double X, double Y) Outside(double distance) =>
            (1.0 + distance / Math.Sqrt(2.0), 1.0 - distance / Math.Sqrt(2.0));

        (double x, double y) = Outside(0.0005);
        Assert.True(projector.TryGetHeightExtremes(x, y, 0.001, out _, out _));
        (x, y) = Outside(0.005);
        Assert.False(projector.TryGetHeightExtremes(x, y, 0.001, out _, out _));
    }

    [Fact]
    public void TryGetHeightExtremes_BeyondAShortEdge_HoldsTheTolerance()
    {
        // A 2 m triangle with a 0.3 m edge, turned 30 degrees so points past that edge stay in its bounding box.
        // One barycentric tolerance taken from the smallest altitude, applied across the short edge whose
        // altitude is seven times larger, accepted points 6.7 mm beyond it for a 1 mm tolerance (live: Drape
        // Split ends landed up to 14 mm past the terrain border, whose triangles meet it with their short edges).
        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
        (double X, double Y) Turn(double x, double y) => ((x * c) - (y * s), (x * s) + (y * c));
        var (bx, by) = Turn(2.0, 0.0);
        var (cx, cy) = Turn(2.0, 0.3);
        double[] vertices = { 0.0, 0.0, 0.0, bx, by, 0.0, cx, cy, 0.0 };
        var projector = new MeshHeightProjector(vertices, 3, new[] { 0, 1, 2 }, 1);
        (double X, double Y) Beyond(double distance) => ((bx + cx) / 2 + distance * c, (by + cy) / 2 + distance * s);

        (double x, double y) = Beyond(0.0005);
        Assert.True(projector.TryGetHeightExtremes(x, y, 0.001, out _, out _));
        (x, y) = Beyond(0.005);
        Assert.False(projector.TryGetHeightExtremes(x, y, 0.001, out _, out _));
    }

    [Fact]
    public void TryProjectZ_OverlappingDistinctHeights_RequiresFallback()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 10.0,
            1.0, 0.0, 10.0,
            0.0, 1.0, 10.0
        };
        int[] faces =
        {
            0, 1, 2,
            3, 4, 5
        };
        var projector = new MeshHeightProjector(vertices, 6, faces, 2);

        bool projected = projector.TryProjectZ(
            0.25,
            0.25,
            sampleZ: 9.0,
            tolerance: 1e-6,
            out _,
            out var status);

        Assert.False(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.RequiresFallback, status);
    }

    [Fact]
    public void TryProjectZ_NearVerticalFaceFootprint_RequiresFallback()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 10.0
        };
        int[] faces = { 0, 1, 2 };
        var projector = new MeshHeightProjector(vertices, 3, faces, 1);

        bool projected = projector.TryProjectZ(
            0.0,
            0.5,
            sampleZ: 5.0,
            tolerance: 1e-6,
            out _,
            out var status);

        Assert.False(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.RequiresFallback, status);
    }

    [Theory]
    [InlineData(0.000000001)]
    [InlineData(0.001)]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    [InlineData(1000000000.0)]
    public void TryProjectZ_UniformlyScaledGeometry_ReturnsScaledHeight(double scale)
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            2.0 * scale, 0.0, 2.0 * scale,
            0.0, 2.0 * scale, 4.0 * scale
        };
        int[] faces = { 0, 1, 2 };
        var projector = new MeshHeightProjector(vertices, 3, faces, 1);

        bool projected = projector.TryProjectZ(
            0.5 * scale,
            0.5 * scale,
            sampleZ: 0.0,
            tolerance: 1e-8 * scale,
            out double z,
            out var status);

        Assert.True(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.Projected, status);
        Assert.Equal(1.5 * scale, z, precision: 10);
    }

    /// <summary>
    /// A point over a wall is projected through Rhino against the triangles of its grid cell alone, so every
    /// triangle whose footprint holds the point must be among them - including a vertical wall face, which the
    /// grid lookup itself skips.
    /// </summary>
    [Fact]
    public void CandidateTrianglesAt_PointOverAWall_IncludesEveryFaceWhoseFootprintHoldsIt()
    {
        const int n = 40;
        var vertices = new List<double>();
        var faces = new List<int>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
                vertices.AddRange(new[] { (double)i, j, i >= 20 ? 3.0 : 0.0 });
        }

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = (j * (n + 1)) + i;
                faces.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 });
            }
        }

        // A vertical wall along x = 19.5, from z 0 to 3.
        int wall = vertices.Count / 3;
        vertices.AddRange(new[] { 19.5, 5.0, 0.0, 19.5, 30.0, 0.0, 19.5, 30.0, 3.0, 19.5, 5.0, 3.0 });
        faces.AddRange(new[] { wall, wall + 1, wall + 2, wall, wall + 2, wall + 3 });

        var projector = new MeshHeightProjector(vertices.ToArray(), vertices.Count / 3, faces.ToArray(), faces.Count / 3);
        foreach ((double x, double y) in new[] { (19.5, 12.25), (7.3, 33.1), (19.5, 5.0), (39.9, 0.1) })
        {
            double[] candidates = projector.CandidateTrianglesAt(x, y);
            for (int f = 0; f < faces.Count / 3; f++)
            {
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                for (int c = 0; c < 3; c++)
                {
                    int v = faces[(f * 3) + c];
                    minX = Math.Min(minX, vertices[v * 3]); maxX = Math.Max(maxX, vertices[v * 3]);
                    minY = Math.Min(minY, vertices[(v * 3) + 1]); maxY = Math.Max(maxY, vertices[(v * 3) + 1]);
                }

                if (x < minX || x > maxX || y < minY || y > maxY)
                    continue;

                bool listed = false;
                for (int t = 0; t < candidates.Length / 9 && !listed; t++)
                {
                    listed = true;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = faces[(f * 3) + c];
                        listed &= candidates[(t * 9) + (c * 3)] == vertices[v * 3] &&
                                  candidates[(t * 9) + (c * 3) + 1] == vertices[(v * 3) + 1] &&
                                  candidates[(t * 9) + (c * 3) + 2] == vertices[(v * 3) + 2];
                    }
                }

                Assert.True(listed, $"face {f} holds ({x}, {y}) in its footprint but is not a candidate");
            }
        }
    }

    [Fact]
    public void TryGetHeightExtremes_StackedFaces_ReturnsBothHeights()
    {
        // Two overlapping triangles, one at z = 0 and one at z = 5.
        double[] vertices =
        {
            0.0, 0.0, 0.0, 4.0, 0.0, 0.0, 0.0, 4.0, 0.0,
            0.0, 0.0, 5.0, 4.0, 0.0, 5.0, 0.0, 4.0, 5.0
        };
        int[] faces = { 0, 1, 2, 3, 4, 5 };
        var projector = new MeshHeightProjector(vertices, 6, faces, 2);

        Assert.True(projector.TryGetHeightExtremes(1.0, 1.0, 1e-8, out double lowest, out double highest));
        Assert.Equal(0.0, lowest, 12);
        Assert.Equal(5.0, highest, 12);
        Assert.False(projector.TryGetHeightExtremes(10.0, 10.0, 1e-8, out _, out _));
    }
}
