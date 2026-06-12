using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

/// <summary>
/// Native-free planner tests: <see cref="RetainingWallPlannerCore.PlanPolylines"/> with
/// buildSolids: false is pure managed math (Point3d is a managed struct), so these run in any test
/// host. Solid Brep generation and curve tessellation are covered separately by the
/// [RhinoNativeFact] tests in <see cref="RetainingWallPlannerGeometryTests"/>.
/// </summary>
public class RetainingWallPlannerLogicTests
{
    private static RetainingWallPlannerCore.PlanResult Plan(double maxWallWidth, params RetainingWallPlannerCore.RailPolyline?[] rails) =>
        RetainingWallPlannerCore.PlanPolylines(rails, maxWallWidth, buildSolids: false);

    private static RetainingWallPlannerCore.RailPolyline Open(params Point3d[] points) => new(points, false);

    private static RetainingWallPlannerCore.RailPolyline Closed(params Point3d[] points) => new(points, true);

    [Fact]
    public void Plan_StraightWall_PairsRailsAndMeasuresWidth()
    {
        var plan = Plan(
            1.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)));

        var wall = Assert.Single(plan.Walls);
        Assert.Equal(2, wall.Rails.ToePoints.Length);
        Assert.Equal(2, wall.Rails.TopPoints.Length);
        Assert.False(wall.Rails.IsClosed);
        Assert.Equal(1.0, wall.Rails.MinWidth, 6);
        Assert.Equal(0.0, wall.Rails.ToePoints[0].Z, 6);
        Assert.Equal(3.0, wall.Rails.TopPoints[0].Z, 6);
    }

    [Fact]
    public void Plan_ShortAuthoredSegment_PreservesStation()
    {
        var plan = Plan(
            1.0,
            Open(
                new Point3d(0, 0, 0),
                new Point3d(4, 0, 0),
                new Point3d(5, 0, 0),
                new Point3d(5, 4, 0)),
            Open(
                new Point3d(0, 1, 3),
                new Point3d(4, 1, 3),
                new Point3d(5, 1, 3),
                new Point3d(5, 5, 3)));

        var wall = Assert.Single(plan.Walls);
        Assert.Contains(
            wall.Rails.ToePoints,
            point => Math.Abs(point.X - 4.0) <= 1e-6 && Math.Abs(point.Y) <= 1e-6);
    }

    [Fact]
    public void Plan_AmbiguousSecondBest_SkipsPairWithReason()
    {
        var plan = Plan(
            2.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)),
            Open(new Point3d(0, 1.2, 4), new Point3d(10, 1.2, 4)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.AmbiguousPair);
    }

    [Fact]
    public void Plan_SubToleranceSpacing_SkipsPair()
    {
        var plan = Plan(
            1.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 0.05, 3), new Point3d(10, 0.05, 3)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SubToleranceWidth);
    }

    [Fact]
    public void Plan_EqualZRails_SkipsPairWhenWallHasNoHeight()
    {
        var plan = Plan(
            1.0,
            Open(new Point3d(0, 0, 1), new Point3d(10, 0, 1)),
            Open(new Point3d(0, 1, 1), new Point3d(10, 1, 1)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SolidFailed);
    }

    [Fact]
    public void Plan_ZeroHeightStart_BuildsTaperedWallRails()
    {
        var plan = Plan(
            1.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 0), new Point3d(10, 1, 4)));

        var wall = Assert.Single(plan.Walls);
        Assert.Equal(2, wall.Rails.ToePoints.Length);
    }

    [Fact]
    public void Plan_ZeroHeightEnd_BuildsTaperedWallRails()
    {
        var plan = Plan(
            1.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 4), new Point3d(10, 1, 0)));

        var wall = Assert.Single(plan.Walls);
        Assert.Equal(2, wall.Rails.ToePoints.Length);
    }

    [Fact]
    public void Plan_ClosedLoopPair_BuildsClosedWallRing()
    {
        var plan = Plan(
            2.0,
            Closed(
                new Point3d(0, 0, 0),
                new Point3d(10, 0, 0),
                new Point3d(10, 8, 0),
                new Point3d(0, 8, 0)),
            Closed(
                new Point3d(-1, -1, 3),
                new Point3d(11, -1, 3),
                new Point3d(11, 9, 3),
                new Point3d(-1, 9, 3)));

        var wall = Assert.Single(plan.Walls);
        Assert.True(wall.Rails.IsClosed);
        Assert.Single(plan.PairLines);
    }

    [Fact]
    public void Plan_ClosedLoopPair_ReversedOuterRailStillBuilds()
    {
        var plan = Plan(
            2.0,
            Closed(
                new Point3d(0, 0, 0),
                new Point3d(10, 0, 0),
                new Point3d(10, 8, 0),
                new Point3d(0, 8, 0)),
            Closed(
                new Point3d(-1, 9, 3),
                new Point3d(11, 9, 3),
                new Point3d(11, -1, 3),
                new Point3d(-1, -1, 3)));

        var wall = Assert.Single(plan.Walls);
        Assert.True(wall.Rails.IsClosed);
    }

    [Fact]
    public void Plan_MixedOpenClosed_RaisesDiagnostic()
    {
        var plan = Plan(
            2.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Closed(
                new Point3d(0, 1, 3),
                new Point3d(10, 1, 3),
                new Point3d(10, 2, 3),
                new Point3d(0, 2, 3)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.MixedOpenClosed);
    }

    [Fact]
    public void Plan_SelfIntersectingRail_RaisesDiagnostic()
    {
        var plan = Plan(
            2.0,
            Open(
                new Point3d(0, 0, 0),
                new Point3d(5, 5, 0),
                new Point3d(0, 5, 0),
                new Point3d(5, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(5, 1, 3)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SelfIntersectingRail);
    }

    [Fact]
    public void Plan_TwoWallCorner_UsesBoundedMiter()
    {
        var plan = Plan(
            1.1,
            Open(new Point3d(-5, 0, 0), new Point3d(0, 0, 0)),
            Open(new Point3d(-5, 1, 3), new Point3d(0, 1, 3)),
            Open(new Point3d(0, 0, 0), new Point3d(0, 5, 0)),
            Open(new Point3d(-1, 0, 3), new Point3d(-1, 5, 3)));

        Assert.Equal(2, plan.Walls.Count);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.CornerResolved);
        Assert.All(plan.Walls, wall => Assert.True(wall.Rails.MinWidth >= 0.11));
    }

    [Fact]
    public void Plan_CrossingWallCenterlines_WarnsButKeepsPairs()
    {
        var plan = Plan(
            1.1,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)),
            Open(new Point3d(5, -5, 0), new Point3d(5, 5, 0)),
            Open(new Point3d(6, -5, 3), new Point3d(6, 5, 3)));

        Assert.Equal(2, plan.Walls.Count);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.CrossingWalls);
    }

    [Fact]
    public void Plan_ThreeRails_ClearPairLeavesNoPairWarningForThird()
    {
        var plan = Plan(
            1.1,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)),
            Open(new Point3d(0, 10, 0), new Point3d(10, 10, 0)));

        Assert.Single(plan.Walls);
        Assert.Contains(plan.Report, entry =>
            entry.Reason == RetainingWallPlannerCore.ReportReason.NoPair &&
            entry.CurveA == 2);
    }

    [Fact]
    public void Plan_NullRail_ReportsAndContinues()
    {
        var plan = Plan(
            1.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            null,
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)));

        Assert.Single(plan.Walls);
    }

    [Fact]
    public void Plan_NearlyClosedPoints_TreatedAsClosedRing()
    {
        // Open flag, but the point sequence returns to its start — closure must be detected.
        var inner = new RetainingWallPlannerCore.RailPolyline(
            new[]
            {
                new Point3d(0, 0, 0),
                new Point3d(10, 0, 0),
                new Point3d(10, 8, 0),
                new Point3d(0, 8, 0),
                new Point3d(0, 0, 0)
            },
            false);
        var outer = new RetainingWallPlannerCore.RailPolyline(
            new[]
            {
                new Point3d(-1, -1, 3),
                new Point3d(11, -1, 3),
                new Point3d(11, 9, 3),
                new Point3d(-1, 9, 3),
                new Point3d(-1, -1, 3)
            },
            false);

        var plan = Plan(2.0, inner, outer);

        var wall = Assert.Single(plan.Walls);
        Assert.True(wall.Rails.IsClosed);
    }
}
