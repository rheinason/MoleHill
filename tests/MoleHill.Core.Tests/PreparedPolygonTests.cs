using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// <see cref="PreparedPolygon"/> must answer exactly what the linear crossing and distance walks
/// answer — it is only allowed to skip work, never to change a verdict. The oracle here is
/// <see cref="GradingGeometry2D"/> itself, sampled densely at edges, vertices and large coordinates.
/// </summary>
public class PreparedPolygonTests
{
    [Theory]
    [InlineData(8)]     // below the index threshold: linear walk
    [InlineData(400)]   // above it: Y-bucketed edges
    public void Contains_MatchesTheLinearCrossingTest(int vertexCount)
    {
        double[] loop = Star(vertexCount, 0.0, 0.0, innerRadius: 4.0, outerRadius: 10.0);
        PreparedPolygon prepared = Require(loop);

        var random = new Random(778);
        for (int trial = 0; trial < 20_000; trial++)
        {
            double x = (random.NextDouble() * 30.0) - 15.0;
            double y = (random.NextDouble() * 30.0) - 15.0;

            Assert.Equal(GradingGeometry2D.PointInPolygon(x, y, loop, vertexCount), prepared.Contains(x, y));
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(400)]
    public void Contains_AtVerticesAndAlongEdges_MatchesTheLinearCrossingTest(int vertexCount)
    {
        double[] loop = Star(vertexCount, 0.0, 0.0, innerRadius: 4.0, outerRadius: 10.0);
        PreparedPolygon prepared = Require(loop);

        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double xi = loop[i * 2], yi = loop[(i * 2) + 1];
            double xj = loop[next * 2], yj = loop[(next * 2) + 1];

            AssertSame(loop, vertexCount, prepared, xi, yi);
            for (int step = 1; step < 5; step++)
            {
                double t = step / 5.0;
                AssertSame(loop, vertexCount, prepared, xi + ((xj - xi) * t), yi + ((yj - yi) * t));
            }
        }
    }

    [Fact]
    public void Contains_LargeCoordinates_MatchesTheLinearCrossingTest()
    {
        double[] loop = Star(300, 1_000_000.0, -2_000_000.0, innerRadius: 4.0, outerRadius: 10.0);
        PreparedPolygon prepared = Require(loop);

        var random = new Random(4114);
        for (int trial = 0; trial < 10_000; trial++)
        {
            double x = 1_000_000.0 + (random.NextDouble() * 30.0) - 15.0;
            double y = -2_000_000.0 + (random.NextDouble() * 30.0) - 15.0;

            Assert.Equal(GradingGeometry2D.PointInPolygon(x, y, loop, 300), prepared.Contains(x, y));
        }
    }

    [Fact]
    public void Contains_ADegenerateHorizontalSliverLoop_MatchesTheLinearCrossingTest()
    {
        // Zero height: the bucket index cannot be built, so this exercises the fallback path.
        double[] loop = { 0.0, 5.0, 10.0, 5.0, 20.0, 5.0, 30.0, 5.0 };
        PreparedPolygon prepared = Require(loop);

        for (double x = -5.0; x <= 35.0; x += 0.5)
        {
            AssertSame(loop, 4, prepared, x, 5.0);
            AssertSame(loop, 4, prepared, x, 4.999);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(400)]
    public void IsWithin_MatchesTheLinearDistanceTest(int vertexCount)
    {
        double[] loop = Star(vertexCount, 0.0, 0.0, innerRadius: 4.0, outerRadius: 10.0);
        PreparedPolygon prepared = Require(loop);

        var random = new Random(9182);
        foreach (double margin in new[] { 0.0, 0.25, 2.0, 40.0 })
        {
            for (int trial = 0; trial < 5_000; trial++)
            {
                double x = (random.NextDouble() * 40.0) - 20.0;
                double y = (random.NextDouble() * 40.0) - 20.0;

                bool expected = GradingGeometry2D.DistanceToPolygon(x, y, loop, vertexCount) <= margin;
                Assert.Equal(expected, prepared.IsWithin(x, y, margin));
            }
        }
    }

    [Fact]
    public void TryCreate_TooFewVertices_ReturnsNull()
    {
        Assert.Null(PreparedPolygon.TryCreate(new[] { 0.0, 0.0, 1.0, 1.0 }, 2));
        Assert.Null(PreparedPolygon.TryCreate(Array.Empty<double>(), 3));
    }

    [Fact]
    public void Contains_LoopWithANonFiniteVertex_StillMatchesTheLinearCrossingTest()
    {
        // Bounds derived from such a loop cannot be trusted to reject, so the linear walk is kept and
        // its answer - whatever it is - must be reproduced exactly.
        double[] loop = { 0.0, 0.0, 10.0, 0.0, 10.0, double.NaN, 0.0, 10.0 };
        PreparedPolygon prepared = Require(loop);

        for (double x = -20.0; x <= 20.0; x += 0.5)
        {
            for (double y = -20.0; y <= 20.0; y += 0.5)
                AssertSame(loop, 4, prepared, x, y);
        }
    }

    [Fact]
    public void CreateAll_DropsLoopsThatAreTooShort()
    {
        var loops = new List<double[]>
        {
            new[] { 0.0, 0.0, 1.0, 0.0, 1.0, 1.0 },
            new[] { 0.0, 0.0, 1.0, 0.0 }
        };

        List<PreparedPolygon> prepared = PreparedPolygon.CreateAll(loops);

        Assert.Single(prepared);
        Assert.Equal(3, prepared[0].VertexCount);
    }

    private static void AssertSame(double[] loop, int vertexCount, PreparedPolygon prepared, double x, double y)
    {
        Assert.Equal(GradingGeometry2D.PointInPolygon(x, y, loop, vertexCount), prepared.Contains(x, y));
    }

    private static PreparedPolygon Require(double[] loop)
    {
        PreparedPolygon? prepared = PreparedPolygon.TryCreate(loop, loop.Length / 2);
        Assert.NotNull(prepared);
        return prepared!;
    }

    /// <summary>Alternating inner/outer radius star — a deliberately re-entrant, many-edged loop.</summary>
    private static double[] Star(int vertexCount, double cx, double cy, double innerRadius, double outerRadius)
    {
        var loop = new double[vertexCount * 2];
        for (int i = 0; i < vertexCount; i++)
        {
            double angle = (i * 2.0 * Math.PI) / vertexCount;
            double radius = (i % 2 == 0) ? outerRadius : innerRadius;
            loop[i * 2] = cx + (Math.Cos(angle) * radius);
            loop[(i * 2) + 1] = cy + (Math.Sin(angle) * radius);
        }

        return loop;
    }
}
