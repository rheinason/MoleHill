using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class TinBoundaryPreparerTests
{
    [Fact]
    public void Prepare_OpenBreaklines_UsesEndpointHullWithoutAddingSyntheticBoundaryVertices()
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

        Assert.Equal(TinBoundaryPreparer.BoundaryMode.InferredEndpointHull, prepared.Mode);
        Assert.False(prepared.UseConvexHull);
        Assert.Equal(z.Length, prepared.ZValues.Length);
        Assert.DoesNotContain(prepared.ZValues, double.IsNaN);
        Assert.Equal((segments.Length / 2) + 4, prepared.Segments.Length / 2);
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
        Assert.True(prepared.ZValues.Any(double.IsNaN));
        Assert.Equal((segments.Length / 2) + 4, prepared.Segments.Length / 2);
    }
}
