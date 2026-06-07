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
            CreateLine(0.0, 0.0, 1.0, 10.0, 10.0, 3.0, preserveInputElevation: true),
            CreateLine(0.0, 10.0, 5.0, 10.0, 0.0, 7.0, preserveInputElevation: false)
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

    [Fact]
    public void SplitAtIntersections_ManyDistantSegments_OnlySplitsIntersectingCandidates()
    {
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>();
        for (int i = 0; i < 200; i++)
        {
            double x = 1000.0 + (i * 20.0);
            constraints.Add(CreateLine(x, 50.0, 0.0, x + 10.0, 50.0, 0.0, preserveInputElevation: false));
        }

        constraints.Add(CreateLine(0.0, 0.0, 1.0, 10.0, 10.0, 3.0, preserveInputElevation: true));
        constraints.Add(CreateLine(0.0, 10.0, 5.0, 10.0, 0.0, 7.0, preserveInputElevation: false));

        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> normalized =
            ConstraintNetworkNormalizer.SplitAtIntersections(constraints, 0.001, out int splitCount);

        Assert.Equal(2, splitCount);
        Assert.Equal(204, normalized.Count);
        Assert.Equal(2, normalized.Count(constraint => constraint.PreserveInputElevation));
        Assert.All(normalized, constraint => Assert.Equal(2, constraint.PointCount));
    }

    private static SurfaceRemesher.ConstraintPolyline CreateLine(
        double ax,
        double ay,
        double az,
        double bx,
        double by,
        double bz,
        bool preserveInputElevation)
    {
        return new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                ax, ay, az,
                bx, by, bz
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: preserveInputElevation);
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
