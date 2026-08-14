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

    private static RetainingWallPlannerCore.PlanResult PlanWithCleanup(
        double maxWallWidth,
        double parsingTolerance,
        double cleanupTolerance,
        params RetainingWallPlannerCore.RailPolyline?[] rails) =>
        RetainingWallPlannerCore.PlanPolylines(
            rails,
            maxWallWidth,
            curveParsingTolerance: parsingTolerance,
            curveCleanupTolerance: cleanupTolerance,
            buildSolids: false);

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

    [Theory]
    [InlineData(0.001)]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void Plan_StraightWall_UniformScale_PreservesTopologyAndRelativeMeasurements(double scale)
    {
        var plan = PlanWithCleanup(
            1.0 * scale,
            0.001 * scale,
            0.001 * scale,
            Open(new Point3d(0, 0, 0), new Point3d(10 * scale, 0, 0)),
            Open(new Point3d(0, 1 * scale, 3 * scale), new Point3d(10 * scale, 1 * scale, 3 * scale)));

        Assert.True(
            plan.Walls.Count == 1,
            string.Join(Environment.NewLine, plan.Report.Select(entry => entry.ToString())));
        var wall = plan.Walls[0];
        Assert.Equal(2, wall.Rails.ToePoints.Length);
        Assert.Equal(2, wall.Rails.TopPoints.Length);
        Assert.Equal(scale, wall.Rails.MinWidth, precision: 8);
        Assert.Equal(3 * scale, wall.Rails.TopPoints[0].Z, precision: 8);
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
        RetainingWallPlannerCore.ReportEntry diagnostic = Assert.Single(
            plan.Report,
            entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SelfIntersectingRail);
        Assert.True(diagnostic.Location.HasValue);
        Assert.Equal(2.5, diagnostic.Location.Value.X, 6);
        Assert.Equal(2.5, diagnostic.Location.Value.Y, 6);
        Assert.Equal(2, diagnostic.FocusSegments.Count);
        Assert.Contains("Split or redraw", diagnostic.Message);
    }

    [Fact]
    public void Plan_TinySelfIntersectionWithinCleanupLimit_RepairsAndReportsInformation()
    {
        var plan = PlanWithCleanup(
            1.1,
            0.001,
            0.05,
            Open(
                new Point3d(0, 0, 0),
                new Point3d(4, 0, 0),
                new Point3d(4.02, 0.02, 0),
                new Point3d(4, 0.02, 0),
                new Point3d(4.02, 0, 0),
                new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)));

        Assert.Single(plan.Walls);
        RetainingWallPlannerCore.ReportEntry repair = Assert.Single(
            plan.Report,
            entry => entry.Reason == RetainingWallPlannerCore.ReportReason.RailDetailSimplified);
        Assert.Equal(RetainingWallPlannerCore.ReportLevel.Info, repair.Level);
        Assert.DoesNotContain(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SelfIntersectingRail);
    }

    [Fact]
    public void Plan_RetracingRail_RejectsNonMonotonicStationMapping()
    {
        var plan = Plan(
            2.0,
            Open(
                new Point3d(0, 0, 0),
                new Point3d(10, 0, 0),
                new Point3d(0, 0.2, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)));

        Assert.Empty(plan.Walls);
        RetainingWallPlannerCore.ReportEntry diagnostic = Assert.Single(
            plan.Report,
            entry => entry.Reason == RetainingWallPlannerCore.ReportReason.InvalidStationMapping);
        Assert.True(diagnostic.Location.HasValue);
        Assert.Single(diagnostic.FocusSegments);
        Assert.Contains("doubles back", diagnostic.Message);
    }

    [Fact]
    public void Plan_TinyRetraceWithinCleanupLimit_RepairsAndReportsInformation()
    {
        var plan = PlanWithCleanup(
            1.1,
            0.001,
            0.12,
            Open(
                new Point3d(0, 0, 0),
                new Point3d(0.6, 0, 0),
                new Point3d(0.5, 0, 0),
                new Point3d(1, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(1, 1, 3)));

        Assert.Single(plan.Walls);
        Assert.Contains(plan.Report, entry =>
            entry.Reason == RetainingWallPlannerCore.ReportReason.RailDetailSimplified &&
            entry.Level == RetainingWallPlannerCore.ReportLevel.Info);
        Assert.DoesNotContain(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.InvalidStationMapping);
    }

    [Fact]
    public void Plan_LongCollinearRetraceBeyondCleanupLimit_RemainsRejected()
    {
        var plan = PlanWithCleanup(
            2.0,
            0.001,
            0.12,
            Open(
                new Point3d(0, 0, 0),
                new Point3d(10, 0, 0),
                new Point3d(0, 0.2, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.InvalidStationMapping);
        Assert.DoesNotContain(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.RailDetailSimplified);
    }

    [Fact]
    public void Plan_PartialLengthOverlap_RejectsIncompleteStationCoverage()
    {
        var plan = Plan(
            2.0,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(5, 1, 3)));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry =>
            entry.Reason == RetainingWallPlannerCore.ReportReason.InvalidStationMapping &&
            entry.Location.HasValue &&
            entry.FocusSegments.Count == 1 &&
            entry.Message.Contains("do not overlap"));
    }

    [Fact]
    public void Plan_DenseRedundantOpenRails_SimplifiesBeforeWallCreation()
    {
        Point3d[] toe = Enumerable.Range(0, 600)
            .Select(index => new Point3d(index * 0.02, 0, index * 0.001))
            .ToArray();
        Point3d[] top = Enumerable.Range(0, 600)
            .Select(index => new Point3d(index * 0.02, 1, 3 + (index * 0.001)))
            .ToArray();

        var wall = Assert.Single(Plan(1.0, Open(toe), Open(top)).Walls);

        Assert.Equal(2, wall.Rails.ToePoints.Length);
        Assert.Equal(2, wall.Rails.TopPoints.Length);
        Assert.Equal(toe[^1].Z, wall.Rails.ToePoints[^1].Z, 9);
        Assert.Equal(top[^1].Z, wall.Rails.TopPoints[^1].Z, 9);
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
        RetainingWallPlannerCore.ReportEntry crossing = Assert.Single(
            plan.Report,
            entry => entry.Reason == RetainingWallPlannerCore.ReportReason.CrossingWalls);
        Assert.Equal(RetainingWallPlannerCore.ReportLevel.Warning, crossing.Level);
        Assert.True(crossing.Location.HasValue);
        Assert.Equal(2, crossing.FocusSegments.Count);
        Assert.Equal(4, crossing.RelatedCurves.Count);
    }

    [Fact]
    public void Plan_VerticallySeparatedWallCenterlinesCross_ReportsInformation()
    {
        var plan = Plan(
            1.1,
            Open(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(10, 1, 3)),
            Open(new Point3d(5, -5, 10), new Point3d(5, 5, 10)),
            Open(new Point3d(6, -5, 13), new Point3d(6, 5, 13)));

        Assert.Equal(2, plan.Walls.Count);
        Assert.Contains(plan.Report, entry =>
            entry.Reason == RetainingWallPlannerCore.ReportReason.CrossingWalls &&
            entry.Level == RetainingWallPlannerCore.ReportLevel.Info &&
            entry.Message.Contains("vertically separated"));
    }

    [Fact]
    public void Plan_OnlyInfiniteCenterlineExtensionsCross_DoesNotReportCrossing()
    {
        var plan = Plan(
            1.1,
            Open(new Point3d(0, 0, 0), new Point3d(2, 0, 0)),
            Open(new Point3d(0, 1, 3), new Point3d(2, 1, 3)),
            Open(new Point3d(5, -1, 0), new Point3d(5, 1, 0)),
            Open(new Point3d(6, -1, 3), new Point3d(6, 1, 3)));

        Assert.Equal(2, plan.Walls.Count);
        Assert.DoesNotContain(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.CrossingWalls);
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
            entry.CurveA == 2 &&
            entry.Location.HasValue &&
            entry.Message.Contains("Max Wall Width"));
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
