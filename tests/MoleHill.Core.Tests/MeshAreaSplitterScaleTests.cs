using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshAreaSplitterScaleTests
{
    [Fact]
    public void GatherCandidates_QueryDwarfsIndex_ReturnsSameCandidatesAsClippedQuery()
    {
        var grid = SpatialHashGrid2D.Build(new[]
        {
            new Bounds2D(0, 1, 0, 1), new Bounds2D(2, 3, 2, 3)
        });
        var candidates = new List<int>();
        var scratch = new SpatialHashGrid2D.QueryScratch(2);
        grid.GatherCandidates(new Bounds2D(-1e12, 1e12, -1e12, 1e12), candidates, scratch);
        Assert.Equal(new[] { 0, 1 }, candidates.OrderBy(i => i));
        grid.GatherCandidates(new Bounds2D(2, 1e12, 2, 1e12), candidates, scratch);
        Assert.Equal(new[] { 1 }, candidates);
        grid.GatherCandidates(new Bounds2D(4, 1e12, 4, 1e12), candidates, scratch);
        Assert.Empty(candidates);
    }

    [Fact]
    public void SplitPreservingTopology_LargeFaceContainingTinyZone_CompletesAndClassifiesInterior()
    {
        var result = MeshAreaSplitter.SplitPreservingTopology(
            new[] { 0.0, 0.0, 0.0, 10000.0, 0.0, 0.0, 0.0, 10000.0, 0.0 }, 3,
            new[] { 0, 1, 2 }, 1,
            new[] { new MeshAreaSplitter.AreaBoundary(new[] { 1.0, 1.0, 2.0, 1.0, 2.0, 2.0, 1.0, 2.0 }, 4) },
            1e-6, out var error);
        Assert.True(result != null, error);
        Assert.Contains(0, result!.FaceAreaIndex);
        Assert.Contains(-1, result.FaceAreaIndex);
    }

    [Fact]
    public void Classify_IndexedRays_MatchesOriginalPredicateAtVerticesAndRandomPoints()
    {
        var polygon = new double[1024];
        for (int i = 0; i < polygon.Length / 2; i++)
        {
            double angle = i * Math.PI * 2 / (polygon.Length / 2);
            double radius = i % 2 == 0 ? 100 : 60;
            polygon[i * 2] = 1000000 + radius * Math.Cos(angle);
            polygon[i * 2 + 1] = -2000000 + radius * Math.Sin(angle);
        }
        var areas = new[]
        {
            new MeshAreaSplitter.AreaBoundary(polygon, polygon.Length / 2),
            new MeshAreaSplitter.AreaBoundary(new[]
            {
                999960.0, -2000040.0, 1000040.0, -2000040.0,
                1000040.0, -1999960.0, 999960.0, -1999960.0
            }, 4)
        };
        var points = new List<(double X, double Y)>();
        foreach (var area in areas)
        for (int i = 0; i < area.VertexCount; i++)
        {
            double x = area.XyVertices[i * 2], y = area.XyVertices[i * 2 + 1];
            points.Add((x, y));
            points.Add((double.BitIncrement(x), double.BitIncrement(y)));
            points.Add((double.BitDecrement(x), double.BitDecrement(y)));
        }
        var random = new Random(7123);
        for (int i = 0; i < 2000; i++)
            points.Add((999880 + random.NextDouble() * 240, -2000120 + random.NextDouble() * 240));

        // Classify needs only centroids; repeated indices let us probe boundary cases directly.
        var vertices = new double[points.Count * 3];
        var faces = new int[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            vertices[i * 3] = points[i].X;
            vertices[i * 3 + 1] = points[i].Y;
            faces[i * 3] = faces[i * 3 + 1] = faces[i * 3 + 2] = i;
        }
        var result = MeshAreaSplitter.Classify(vertices, points.Count, faces, points.Count, areas, 0, out var error);
        Assert.True(result != null, error);
        for (int i = 0; i < points.Count; i++)
        {
            double x = (points[i].X + points[i].X + points[i].X) / 3;
            double y = (points[i].Y + points[i].Y + points[i].Y) / 3;
            int expected = -1;
            for (int area = 0; area < areas.Length; area++)
                if (GradingGeometry2D.PointInPolygon(x, y, areas[area].XyVertices, areas[area].VertexCount))
                    expected = area;
            Assert.Equal(expected, result!.FaceAreaIndex[i]);
        }
    }
}
