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

    // Kerbs: rails drawn to a point or round nose meet at the end. The meeting is a shaped end, not a
    // collapsed wall, so the pair is kept; a 2.5D terrain cannot hold both rails at one plan point, so the
    // top rail handed to the terrain stops a sliver short of the toe.
    [Fact]
    public void Plan_RailsConvergingToPoint_KeepsWallAndTrimsTerrainTopRail()
    {
        var toe = Enumerable.Range(0, 81).Select(i => new Point3d(i * 0.1, 0, 0)).ToArray();
        var top = Enumerable.Range(0, 81)
            .Select(i => i * 0.1)
            .Select(x => new Point3d(x, x < 7.5 ? 0.05 : 0.05 * (8.0 - x) / 0.5, 0.15))
            .ToArray();

        var plan = Plan(0.2, Open(toe), Open(top));

        var wall = Assert.Single(plan.Walls);
        Assert.InRange(wall.Rails.MinWidth, 0.025, 0.05 + 1e-9); // the wider half of the taper counts as body
        Assert.Equal(toe.Length, wall.Rails.ToePoints.Length);
        Assert.Equal(top.Length - 1, wall.Rails.TopPoints.Length);
        Assert.True(wall.Rails.TopPoints[^1].X < 8.0);
        Assert.DoesNotContain(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SubToleranceWidth);
    }

    [Fact]
    public void Plan_RailsMeetingThroughRoundNose_KeepsWall()
    {
        const double gap = 0.15, r = gap * 0.5;
        var toe = Enumerable.Range(0, 61).Select(i => new Point3d(i * 0.1, 0, 0)).ToList();
        var top = Enumerable.Range(0, 61).Select(i => new Point3d(i * 0.1, gap, 0.15)).ToList();
        for (int k = 1; k <= 8; k++)
        {
            double a = k / 8.0 * Math.PI / 2.0;
            toe.Add(new Point3d(6.0 + r * Math.Sin(a), r - r * Math.Cos(a), 0));
            top.Add(new Point3d(6.0 + r * Math.Sin(a), r + r * Math.Cos(a), 0.15));
        }

        var plan = Plan(0.5, Open(toe.ToArray()), Open(top.ToArray()));

        var wall = Assert.Single(plan.Walls);
        Assert.InRange(wall.Rails.MinWidth, gap * 0.5, gap + 1e-9);
        Assert.Equal(toe.Count, wall.Rails.ToePoints.Length);
        Assert.True(wall.Rails.TopPoints.Length < top.Count);
    }

    [Fact]
    public void Plan_RailsTouchingMidway_StillReadAsNarrow()
    {
        // An interior pinch is not a shaped end: the body gap is the pinch.
        var toe = Enumerable.Range(0, 81).Select(i => new Point3d(i * 0.1, 0, 0)).ToArray();
        var top = Enumerable.Range(0, 81)
            .Select(i => i * 0.1)
            .Select(x => new Point3d(x, Math.Abs(x - 4.0) < 0.35 ? 0.0 : 0.05, 0.15))
            .ToArray();

        var plan = Plan(0.2, Open(toe), Open(top));

        Assert.Empty(plan.Walls);
        Assert.Contains(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SubToleranceWidth);
    }

    [Theory]
    [InlineData(new[] { 1.0, 1.0, 1.0, 1.0 }, false, 1.0)]
    [InlineData(new[] { 0.0, 0.4, 1.0, 1.0, 0.9, 0.0 }, false, 0.9)]
    [InlineData(new[] { 1.0, 1.0, 0.2, 1.0, 1.0 }, false, 0.2)]
    [InlineData(new[] { 0.0, 0.4, 1.0, 1.0 }, true, 0.0)]
    [InlineData(new[] { 0.01, 0.01, 0.01 }, false, 0.01)]
    public void BodyGap_IgnoresOnlyNarrowEndRunsOfOpenWalls(double[] gaps, bool closed, double expected)
    {
        Assert.Equal(expected, RetainingWallPlannerCore.BodyGap(gaps, closed), 9);
    }

    // A kerb cut: the solid is built only where the wall has a section, and each piece is extended to the
    // interpolated point where its section vanishes.
    [Fact]
    public void SplitAtVanishingSections_KerbCut_SplitsIntoTwoTaperedPieces()
    {
        var toe = Enumerable.Range(0, 11).Select(i => new Point3d(i, 0, 0)).ToArray();
        double[] heights = { 1, 1, 1, 0.5, 0, 0, 0, 0.5, 1, 1, 1 };
        var top = heights.Select((h, i) => new Point3d(i, 0.5, h)).ToArray();

        var pieces = RetainingWallBrepBuilder.SplitAtVanishingSections(toe, top, isClosed: false, epsilon: 0.01);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.False(piece.IsClosed));
        Assert.Equal(0.0, pieces[0].Toe[0].X, 9);
        Assert.Equal(3.98, pieces[0].Toe[^1].X, 9);
        Assert.Equal(0.01, pieces[0].Top[^1].Z, 9);
        Assert.Equal(6.02, pieces[1].Toe[0].X, 9);
        Assert.Equal(10.0, pieces[1].Toe[^1].X, 9);
    }

    [Fact]
    public void SplitAtVanishingSections_RingWithOneCut_OpensIntoOnePiece()
    {
        int n = 24;
        var toe = new Point3d[n];
        var top = new Point3d[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2.0 * Math.PI * i / n;
            toe[i] = new Point3d(3.0 * Math.Cos(a), 3.0 * Math.Sin(a), 0);
            top[i] = new Point3d(2.9 * Math.Cos(a), 2.9 * Math.Sin(a), i == 5 ? 0.0 : 0.2);
        }

        var pieces = RetainingWallBrepBuilder.SplitAtVanishingSections(toe, top, isClosed: true, epsilon: 0.001);

        var piece = Assert.Single(pieces);
        Assert.False(piece.IsClosed);
        Assert.Equal(n + 1, piece.Toe.Length); // the 23 other stations plus an interpolated one at each side of the cut
    }

    [Fact]
    public void SplitAtVanishingSections_NoCut_ReturnsWallWhole()
    {
        var toe = new[] { new Point3d(0, 0, 0), new Point3d(5, 0, 0) };
        var top = new[] { new Point3d(0, 1, 2), new Point3d(5, 1, 2) };

        var piece = Assert.Single(RetainingWallBrepBuilder.SplitAtVanishingSections(toe, top, isClosed: false, epsilon: 0.01));

        Assert.Same(toe, piece.Toe);
    }

    // Wall Height (freestanding): rails draped onto level ground sit at one height; the height is what
    // makes them a wall, and the terrain's rails stay on the ground.
    [Fact]
    public void Plan_WallHeight_LevelRailsBecomeAWallAndStayOnTheGround()
    {
        var plan = RetainingWallPlannerCore.PlanPolylines(
            new RetainingWallPlannerCore.RailPolyline?[]
            {
                Open(new Point3d(0, 0, 5), new Point3d(10, 0, 5)),
                Open(new Point3d(0, 0.3, 5), new Point3d(10, 0.3, 5)),
            },
            1.0,
            buildSolids: false,
            wallHeight: 0.15);

        var wall = Assert.Single(plan.Walls);
        Assert.All(wall.Rails.ToePoints, p => Assert.Equal(5.0, p.Z, 9));
        Assert.All(wall.Rails.TopPoints, p => Assert.Equal(5.0, p.Z, 9));
    }

    [Fact]
    public void Plan_WallHeight_SlopingRailsKeepTheirGroundHeights()
    {
        var plan = RetainingWallPlannerCore.PlanPolylines(
            new RetainingWallPlannerCore.RailPolyline?[]
            {
                Open(new Point3d(0, 0, 2), new Point3d(10, 0, 2)),
                Open(new Point3d(0, 0.3, 2.4), new Point3d(10, 0.3, 2.4)),
            },
            1.0,
            buildSolids: false,
            wallHeight: 0.5);

        var wall = Assert.Single(plan.Walls);
        Assert.All(wall.Rails.ToePoints, p => Assert.Equal(2.0, p.Z, 9));
        Assert.All(wall.Rails.TopPoints, p => Assert.Equal(2.4, p.Z, 9));
    }

    [Fact]
    public void Plan_NoWallHeight_LevelRailsStillHaveNoHeight()
    {
        var plan = RetainingWallPlannerCore.PlanPolylines(
            new RetainingWallPlannerCore.RailPolyline?[]
            {
                Open(new Point3d(0, 0, 5), new Point3d(10, 0, 5)),
                Open(new Point3d(0, 0.3, 5), new Point3d(10, 0.3, 5)),
            },
            1.0,
            buildSolids: false);

        Assert.Empty(plan.Walls);
    }
}
