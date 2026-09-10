using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Stitching walks a welded segment soup into chains through flat CSR node incidence and two reusable
/// chain buffers, rather than a list per node and a linked-list node per point. These pin the
/// behaviour that rewrite has to preserve: chain order, open versus closed classification, branch
/// handling, and determinism.
/// </summary>
public class ContourStitchingTests
{
    private const double Weld = 1e-6;

    [Fact]
    public void Generate_ClosedRing_IsReportedClosedAndDoesNotRepeatItsFirstPoint()
    {
        // A cone: any level between its rim and apex crosses as one closed ring.
        (double[] vertices, int[] faces) = Cone(24, radius: 10.0, height: 10.0);

        List<ContourLevel> levels = ContourGenerator.Generate(
            vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { 5.0 }, Weld);

        ContourPolyline ring = Assert.Single(Assert.Single(levels).Polylines);
        Assert.True(ring.IsClosed);
        Assert.True(ring.PointCount > 3);

        double firstX = ring.PointsXyz[0];
        double firstY = ring.PointsXyz[1];
        double lastX = ring.PointsXyz[(ring.PointCount - 1) * 3];
        double lastY = ring.PointsXyz[((ring.PointCount - 1) * 3) + 1];
        Assert.False(Math.Abs(firstX - lastX) < 1e-9 && Math.Abs(firstY - lastY) < 1e-9,
            "A closed polyline must not repeat its first point.");
    }

    [Fact]
    public void Generate_OpenChain_RunsFromOneMeshEdgeToTheOther()
    {
        // A plane tilted in X only: each level is one straight line right across the sheet.
        (double[] vertices, int[] faces) = Sheet(20, (x, _) => x);

        List<ContourLevel> levels = ContourGenerator.Generate(
            vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { 9.5 }, Weld);

        ContourPolyline line = Assert.Single(Assert.Single(levels).Polylines);
        Assert.False(line.IsClosed);
        Assert.True(line.PointCount >= 2);

        // Both ends sit on the sheet boundary, so the chain was not split part-way.
        double firstY = line.PointsXyz[1];
        double lastY = line.PointsXyz[((line.PointCount - 1) * 3) + 1];
        Assert.Equal(0.0, Math.Min(firstY, lastY), 6);
        Assert.Equal(19.0, Math.Max(firstY, lastY), 6);

        for (int i = 0; i < line.PointCount; i++)
            Assert.Equal(9.5, line.PointsXyz[i * 3], 6);
    }

    [Fact]
    public void Generate_EveryPointSitsAtItsLevel()
    {
        (double[] vertices, int[] faces) = Sheet(40, (x, y) => (Math.Sin(x * 0.3) * 6.0) + (Math.Cos(y * 0.22) * 5.0));
        var requested = new[] { -6.0, -2.0, 0.0, 3.0, 7.0 };

        List<ContourLevel> levels = ContourGenerator.Generate(
            vertices, vertices.Length / 3, faces, faces.Length / 3, requested, Weld);

        Assert.NotEmpty(levels);
        foreach (ContourLevel level in levels)
        {
            Assert.NotEmpty(level.Polylines);
            foreach (ContourPolyline polyline in level.Polylines)
            {
                Assert.True(polyline.PointCount >= 2);
                for (int i = 0; i < polyline.PointCount; i++)
                    Assert.Equal(level.Z, polyline.PointsXyz[(i * 3) + 2], 6);
            }
        }
    }

    [Fact]
    public void Generate_SegmentHeavyJob_IsIdenticalAcrossRuns()
    {
        (double[] vertices, int[] faces) = Sheet(60, (x, y) => (Math.Sin(x * 0.3) * 6.0) + (Math.Cos(y * 0.22) * 5.0));
        var requested = new List<double>();
        for (double z = -10.0; z <= 10.0; z += 0.5)
            requested.Add(z);

        List<ContourLevel> first = ContourGenerator.Generate(
            vertices, vertices.Length / 3, faces, faces.Length / 3, requested, Weld);
        List<ContourLevel> second = ContourGenerator.Generate(
            vertices, vertices.Length / 3, faces, faces.Length / 3, requested, Weld);

        Assert.Equal(first.Count, second.Count);
        for (int levelIndex = 0; levelIndex < first.Count; levelIndex++)
        {
            Assert.Equal(first[levelIndex].Z, second[levelIndex].Z);
            Assert.Equal(first[levelIndex].Polylines.Count, second[levelIndex].Polylines.Count);
            for (int i = 0; i < first[levelIndex].Polylines.Count; i++)
            {
                ContourPolyline a = first[levelIndex].Polylines[i];
                ContourPolyline b = second[levelIndex].Polylines[i];
                Assert.Equal(a.IsClosed, b.IsClosed);
                Assert.Equal(a.PointsXyz, b.PointsXyz);
            }
        }
    }

    [Fact]
    public void Generate_LevelAboveAndBelowTheMesh_EmitsNothing()
    {
        (double[] vertices, int[] faces) = Sheet(10, (_, _) => 0.0);

        List<ContourLevel> levels = ContourGenerator.Generate(
            vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { -100.0, 100.0 }, Weld);

        Assert.All(levels, level => Assert.Empty(level.Polylines));
    }

    private static (double[] Vertices, int[] Faces) Sheet(int n, Func<double, double, double> height)
    {
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = (j * n) + i;
                vertices[v * 3] = i;
                vertices[(v * 3) + 1] = j;
                vertices[(v * 3) + 2] = height(i, j);
            }
        }

        var faces = new int[(n - 1) * (n - 1) * 6];
        int f = 0;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = (j * n) + i;
                int v10 = v00 + 1;
                int v01 = v00 + n;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, faces);
    }

    private static (double[] Vertices, int[] Faces) Cone(int segments, double radius, double height)
    {
        var vertices = new double[(segments + 1) * 3];
        for (int i = 0; i < segments; i++)
        {
            double angle = (i * 2.0 * Math.PI) / segments;
            vertices[i * 3] = Math.Cos(angle) * radius;
            vertices[(i * 3) + 1] = Math.Sin(angle) * radius;
            vertices[(i * 3) + 2] = 0.0;
        }

        int apex = segments;
        vertices[apex * 3] = 0.0;
        vertices[(apex * 3) + 1] = 0.0;
        vertices[(apex * 3) + 2] = height;

        var faces = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            faces[i * 3] = i;
            faces[(i * 3) + 1] = (i + 1) % segments;
            faces[(i * 3) + 2] = apex;
        }

        return (vertices, faces);
    }
}
