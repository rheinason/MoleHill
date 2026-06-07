using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class ConstraintNetworkNormalizerTests
{
    [Fact]
    public void SplitAtIntersections_CrossingSegments_SplitsBothConstraints()
    {
        var constraints = new[]
        {
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    0.0, 0.0, 1.0,
                    10.0, 10.0, 3.0
                },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: true),
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    0.0, 10.0, 5.0,
                    10.0, 0.0, 7.0
                },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: false)
        };

        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> normalized =
            ConstraintNetworkNormalizer.SplitAtIntersections(constraints, 0.001, out int splitCount);

        Assert.Equal(2, splitCount);
        Assert.Equal(4, normalized.Count);
        Assert.Equal(2, normalized.Count(constraint => constraint.PreserveInputElevation));
        Assert.Equal(2, normalized.Count(constraint => !constraint.PreserveInputElevation));

        Assert.All(normalized, constraint =>
        {
            Assert.Equal(2, constraint.PointCount);
            bool touchesIntersection =
                HasPoint(constraint, 0, 5.0, 5.0) ||
                HasPoint(constraint, 1, 5.0, 5.0);
            Assert.True(touchesIntersection, "Expected each split segment to touch the crossing point.");
        });
    }

    private static bool HasPoint(
        SurfaceRemesher.ConstraintPolyline constraint,
        int pointIndex,
        double expectedX,
        double expectedY)
    {
        double dx = constraint.Points[pointIndex * 3] - expectedX;
        double dy = constraint.Points[(pointIndex * 3) + 1] - expectedY;
        return (dx * dx) + (dy * dy) <= 1e-12;
    }
}
