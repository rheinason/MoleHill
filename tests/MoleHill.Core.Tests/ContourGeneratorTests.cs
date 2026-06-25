using System.Linq;
using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class ContourGeneratorTests
{
    [Fact]
    public void Generate_TiltedPlane_ProducesOpenLineAtLevel()
    {
        // A 10x10 quad with z = x, split into two triangles.
        double[] vertices =
        {
            0, 0, 0,
            10, 0, 10,
            10, 10, 10,
            0, 10, 0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var levels = ContourGenerator.Generate(vertices, 4, faces, 2, new[] { 5.0 }, 0.001);

        Assert.Single(levels);
        ContourLevel level = levels[0];
        Assert.Equal(5.0, level.Z, 9);
        Assert.Single(level.Polylines);
        ContourPolyline polyline = level.Polylines[0];
        Assert.False(polyline.IsClosed);
        Assert.True(polyline.PointCount >= 2);
        // The contour at z=5 runs along x=5 (z=5), spanning y from 0 to 10.
        for (int i = 0; i < polyline.PointCount; i++)
        {
            Assert.Equal(5.0, polyline.PointsXyz[i * 3], 6);     // x
            Assert.Equal(5.0, polyline.PointsXyz[i * 3 + 2], 6); // z
        }
        double minY = Enumerable.Range(0, polyline.PointCount).Min(i => polyline.PointsXyz[i * 3 + 1]);
        double maxY = Enumerable.Range(0, polyline.PointCount).Max(i => polyline.PointsXyz[i * 3 + 1]);
        Assert.Equal(0.0, minY, 6);
        Assert.Equal(10.0, maxY, 6);
    }

    [Fact]
    public void Generate_Pyramid_ProducesClosedLoop()
    {
        double[] vertices =
        {
            0, 0, 0,
            10, 0, 0,
            10, 10, 0,
            0, 10, 0,
            5, 5, 10
        };
        int[] faces = { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 };

        var levels = ContourGenerator.Generate(vertices, 5, faces, 4, new[] { 5.0 }, 0.001);

        Assert.Single(levels);
        Assert.Single(levels[0].Polylines);
        ContourPolyline loop = levels[0].Polylines[0];
        Assert.True(loop.IsClosed, "mid-level contour around an apex should be a closed loop");
        Assert.Equal(4, loop.PointCount);
        for (int i = 0; i < loop.PointCount; i++)
            Assert.Equal(5.0, loop.PointsXyz[i * 3 + 2], 6); // all at z=5
    }

    [Fact]
    public void Generate_MultipleLevels_OnePassProducesEach()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 10, 10, 10, 10, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var levels = ContourGenerator.Generate(vertices, 4, faces, 2, new[] { 2.5, 5.0, 7.5 }, 0.001);

        Assert.Equal(3, levels.Count);
        Assert.Equal(new[] { 2.5, 5.0, 7.5 }, levels.Select(l => l.Z));
        Assert.All(levels, l => Assert.NotEmpty(l.Polylines));
    }

    [Fact]
    public void Generate_FlatMesh_ProducesNothing()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var levels = ContourGenerator.Generate(vertices, 4, faces, 2, new[] { 0.0, 1.0 }, 0.001);

        Assert.Empty(levels);
    }

    [Fact]
    public void Generate_LevelAtVertexElevation_StillProducesContour()
    {
        // Regression: a contour level that equals a face's zmax (e.g. a graded pad's rim, where two
        // vertices sit at the exact design elevation and the toe is below) was silently dropped by the
        // old strict `< zmax` range bound. Quad x in [0,10]: the x=0 edge is at z=5, the x=10 edge at
        // z=0; split into two triangles. The z=5 contour runs along the x=0 edge.
        double[] vertices =
        {
            0, 0, 5,    // v0
            0, 10, 5,   // v1
            10, 0, 0,   // v2
            10, 10, 0   // v3
        };
        int[] faces = { 0, 2, 1, 1, 2, 3 };

        var levels = ContourGenerator.Generate(vertices, 4, faces, 2, new[] { 5.0 }, 0.001);

        Assert.Single(levels);
        ContourLevel level = levels[0];
        Assert.Equal(5.0, level.Z, 9);
        Assert.Single(level.Polylines);
        ContourPolyline polyline = level.Polylines[0];
        Assert.True(polyline.PointCount >= 2);
        for (int i = 0; i < polyline.PointCount; i++)
        {
            Assert.Equal(0.0, polyline.PointsXyz[i * 3], 6);     // x == 0 (the rim edge)
            Assert.Equal(5.0, polyline.PointsXyz[i * 3 + 2], 6); // z == 5
        }
        double minY = Enumerable.Range(0, polyline.PointCount).Min(i => polyline.PointsXyz[i * 3 + 1]);
        double maxY = Enumerable.Range(0, polyline.PointCount).Max(i => polyline.PointsXyz[i * 3 + 1]);
        Assert.Equal(0.0, minY, 6);
        Assert.Equal(10.0, maxY, 6);
    }

    [Fact]
    public void Generate_LevelOutsideRange_Skipped()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 10, 10, 10, 10, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var levels = ContourGenerator.Generate(vertices, 4, faces, 2, new[] { 50.0 }, 0.001);

        Assert.Empty(levels);
    }
}
