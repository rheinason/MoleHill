using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class BreaklineHeightConflictsTests
{
    private static SurfaceRemesher.ConstraintPolyline Line(params double[] xyz) => new(xyz, xyz.Length / 3, IsClosed: false);

    [Fact]
    public void Find_RailAboveAContour_ReportsTheCrossingAndBothHeights()
    {
        var rail = Line(0, 5, 12.5, 10, 5, 12.5);
        var contour = Line(5, 0, 10, 5, 10, 10);

        var conflicts = BreaklineHeightConflicts.Find(new[] { rail }, new[] { contour }, 0.001);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(5.0, conflict.X, 9);
        Assert.Equal(5.0, conflict.Y, 9);
        Assert.Equal(12.5, conflict.ZA, 9);
        Assert.Equal(10.0, conflict.ZB, 9);
    }

    [Fact]
    public void Find_CrossingAtTheSameHeight_IsNoConflict()
    {
        var rail = Line(0, 5, 9, 10, 5, 11);    // 10.0 at x = 5
        var contour = Line(5, 0, 10, 5, 10, 10);

        Assert.Empty(BreaklineHeightConflicts.Find(new[] { rail }, new[] { contour }, 0.001));
    }

    [Fact]
    public void Find_LinesThatOnlyTouchAtAVertex_AreNoConflict()
    {
        var rail = Line(0, 5, 12, 5, 5, 12);
        var contour = Line(5, 5, 10, 5, 10, 10);

        Assert.Empty(BreaklineHeightConflicts.Find(new[] { rail }, new[] { contour }, 0.001));
    }

    [Fact]
    public void Find_ClosedRing_ChecksItsClosingSegment()
    {
        var ring = new SurfaceRemesher.ConstraintPolyline(new double[] { 0, 0, 3, 10, 0, 3, 10, 10, 3, 0, 10, 3 }, 4, IsClosed: true);
        var crossing = Line(-5, 5, 0, 5, 5, 0);   // crosses only the closing edge x = 0

        var conflict = Assert.Single(BreaklineHeightConflicts.Find(new[] { ring }, new[] { crossing }, 0.001));
        Assert.Equal(0.0, conflict.X, 9);
    }
}
