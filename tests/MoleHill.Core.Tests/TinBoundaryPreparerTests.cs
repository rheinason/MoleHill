using MoleHill.Core.Engine;
using System.Diagnostics;
using Xunit;

namespace MoleHill.Core.Tests;

public class TinBoundaryPreparerTests
{
    [Fact]
    public void Prepare_OpenBreaklinesWithoutBoundary_UsesTriangleNetConvexHull()
    {
        var xy = new[]
        {
            1.0, 8.0,
            5.0, 8.0,
            9.0, 8.0,
            1.0, 5.0,
            5.0, 5.0,
            9.0, 5.0,
            1.0, 2.0,
            5.0, 2.0,
            9.0, 2.0
        };
        var z = Enumerable.Repeat(0.0, xy.Length / 2).ToArray();
        var segments = new[]
        {
            0, 1, 1, 2,
            3, 4, 4, 5,
            6, 7, 7, 8
        };

        var prepared = TinBoundaryPreparer.Prepare(
            xy,
            z,
            segments,
            Array.Empty<TinBoundaryPreparer.BoundaryPolyline>(),
            0.1);

        Assert.Equal(TinBoundaryPreparer.BoundaryMode.None, prepared.Mode);
        Assert.True(prepared.UseConvexHull);
        Assert.Equal(z.Length, prepared.ZValues.Length);
        Assert.DoesNotContain(prepared.ZValues, double.IsNaN);
        Assert.Same(segments, prepared.Segments);
        Assert.Equal(xy.Length, prepared.XyCoords.Length);
    }

    [Fact]
    public void Prepare_ExplicitBoundary_AddsBoundaryLoopVerticesWithDeferredZ()
    {
        var xy = new[]
        {
            1.0, 8.0,
            5.0, 8.0,
            9.0, 8.0,
            1.0, 2.0,
            5.0, 2.0,
            9.0, 2.0
        };
        var z = Enumerable.Repeat(0.0, xy.Length / 2).ToArray();
        var segments = new[]
        {
            0, 1, 1, 2,
            3, 4, 4, 5
        };

        var boundary = new TinBoundaryPreparer.BoundaryPolyline(
            new[]
            {
                0.0, 9.0, 0.0,
                10.0, 9.0, 0.0,
                10.0, 1.0, 0.0,
                0.0, 1.0, 0.0,
                0.0, 9.0, 0.0
            },
            PointCount: 5,
            IsClosed: true);

        var prepared = TinBoundaryPreparer.Prepare(
            xy,
            z,
            segments,
            new[] { boundary },
            0.1);

        Assert.Equal(TinBoundaryPreparer.BoundaryMode.Explicit, prepared.Mode);
        Assert.False(prepared.UseConvexHull);
        Assert.True(prepared.ZValues.Length > z.Length);
        Assert.Contains(prepared.ZValues, double.IsNaN);
        Assert.Equal((segments.Length / 2) + 4, prepared.Segments.Length / 2);
    }

    [Fact]
    public void Prepare_ExplicitBoundary_ReusesCoincidentExistingVertices()
    {
        var xy = new[]
        {
            0.0, 0.0,
            10.0, 0.0,
            10.0, 10.0,
            0.0, 10.0
        };
        var z = Enumerable.Repeat(0.0, xy.Length / 2).ToArray();
        var boundary = new TinBoundaryPreparer.BoundaryPolyline(
            new[]
            {
                0.0, 0.0, 0.0,
                10.0, 0.0, 0.0,
                10.0, 10.0, 0.0,
                0.0, 10.0, 0.0,
                0.0, 0.0, 0.0
            },
            PointCount: 5,
            IsClosed: true);

        var prepared = TinBoundaryPreparer.Prepare(
            xy,
            z,
            Array.Empty<int>(),
            new[] { boundary },
            0.1);

        Assert.Equal(TinBoundaryPreparer.BoundaryMode.Explicit, prepared.Mode);
        Assert.False(prepared.UseConvexHull);
        Assert.Equal(xy.Length, prepared.XyCoords.Length);
        Assert.Equal(z.Length, prepared.ZValues.Length);
        Assert.DoesNotContain(prepared.ZValues, double.IsNaN);
        Assert.Equal(4, prepared.Segments.Length / 2);
    }

    [Fact]
    public void Prepare_DenseOpenConstraintEndpoints_DoesNotInferBoundary()
    {
        const int hullPointCount = 10_000;
        var xy = new double[hullPointCount * 4];
        var z = new double[hullPointCount * 2];
        var segments = new int[hullPointCount * 2];
        for (int i = 0; i < hullPointCount; i++)
        {
            double angle = i * Math.PI * 2.0 / hullPointCount;
            int outer = i * 2;
            int inner = outer + 1;
            xy[outer * 2] = Math.Cos(angle) * 10_000.0;
            xy[outer * 2 + 1] = Math.Sin(angle) * 10_000.0;
            xy[inner * 2] = Math.Cos(angle) * 9_000.0;
            xy[inner * 2 + 1] = Math.Sin(angle) * 9_000.0;
            segments[i * 2] = outer;
            segments[i * 2 + 1] = inner;
        }

        var timer = Stopwatch.StartNew();
        var prepared = TinBoundaryPreparer.Prepare(
            xy,
            z,
            segments,
            Array.Empty<TinBoundaryPreparer.BoundaryPolyline>(),
            0.01);
        timer.Stop();

        Assert.Equal(TinBoundaryPreparer.BoundaryMode.None, prepared.Mode);
        Assert.True(prepared.UseConvexHull);
        Assert.Same(segments, prepared.Segments);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Dense endpoint hull preparation took {timer.Elapsed}.");
    }
}
