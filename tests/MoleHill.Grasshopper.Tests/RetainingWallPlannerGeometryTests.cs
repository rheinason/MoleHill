using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

/// <summary>
/// Native-runtime planner tests: curve tessellation and solid Brep generation need the Rhino native
/// runtime, so these are [RhinoNativeFact] and skip in hosts without it. All pairing/corner/width
/// planning logic is covered native-free in <see cref="RetainingWallPlannerLogicTests"/>.
/// </summary>
public class RetainingWallPlannerGeometryTests
{
    [RhinoNativeFact]
    public void Plan_StraightWall_BuildsSolidAndStrip()
    {
        var plan = RetainingWallPlannerCore.Plan(
            new Curve[]
            {
                new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
                new LineCurve(new Point3d(0, 1, 3), new Point3d(10, 1, 3))
            },
            1.0);

        var wall = Assert.Single(plan.Walls);
        Brep wallBrep = Assert.Single(wall.Breps);
        Assert.True(wallBrep.IsSolid);
        Assert.Equal(2, wall.Rails.ToePoints.Length);
        Assert.Equal(2, wall.Rails.TopPoints.Length);
        Assert.False(wall.Rails.IsClosed);
        Assert.Equal(1.0, wall.Rails.MinWidth, 6);
    }

    [RhinoNativeFact]
    public void Plan_ZeroHeightStart_BuildsTaperedWall()
    {
        var plan = RetainingWallPlannerCore.Plan(
            new Curve[]
            {
                new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
                new LineCurve(new Point3d(0, 1, 0), new Point3d(10, 1, 4))
            },
            1.0);

        var wall = Assert.Single(plan.Walls);
        Brep wallBrep = Assert.Single(wall.Breps);
        Assert.Equal(2, wall.Rails.ToePoints.Length);
    }

    [RhinoNativeFact]
    public void Plan_ZeroHeightEnd_BuildsTaperedWall()
    {
        var plan = RetainingWallPlannerCore.Plan(
            new Curve[]
            {
                new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
                new LineCurve(new Point3d(0, 1, 4), new Point3d(10, 1, 0))
            },
            1.0);

        var wall = Assert.Single(plan.Walls);
        Brep wallBrep = Assert.Single(wall.Breps);
        Assert.Equal(2, wall.Rails.ToePoints.Length);
    }

    [RhinoNativeFact]
    public void Plan_ClosedLoopPair_BuildsClosedSolidWallRing()
    {
        var plan = RetainingWallPlannerCore.Plan(
            new Curve[]
            {
                ClosedPolyline(
                    new Point3d(0, 0, 0),
                    new Point3d(10, 0, 0),
                    new Point3d(10, 8, 0),
                    new Point3d(0, 8, 0)),
                ClosedPolyline(
                    new Point3d(-1, -1, 3),
                    new Point3d(11, -1, 3),
                    new Point3d(11, 9, 3),
                    new Point3d(-1, 9, 3))
            },
            2.0);

        var wall = Assert.Single(plan.Walls);
        Assert.True(wall.Rails.IsClosed);
        Brep wallBrep = Assert.Single(wall.Breps);
        Assert.True(wallBrep.IsSolid);
        Assert.Single(plan.PairLines);
    }

    [RhinoNativeFact]
    public void Plan_ClosedLoopPair_ReversedOuterRailStillBuilds()
    {
        var plan = RetainingWallPlannerCore.Plan(
            new Curve[]
            {
                ClosedPolyline(
                    new Point3d(0, 0, 0),
                    new Point3d(10, 0, 0),
                    new Point3d(10, 8, 0),
                    new Point3d(0, 8, 0)),
                ClosedPolyline(
                    new Point3d(-1, 9, 3),
                    new Point3d(11, 9, 3),
                    new Point3d(11, -1, 3),
                    new Point3d(-1, -1, 3))
            },
            2.0);

        var wall = Assert.Single(plan.Walls);
        Assert.True(wall.Rails.IsClosed);
        Brep wallBrep = Assert.Single(wall.Breps);
        Assert.True(wallBrep.IsSolid);
    }

    // A kerb dropped to zero height for a crossing: one lofted solid collapsed through the cut and the wall
    // published no solid at all. Now one watertight solid each side, tapering into the cut.
    [RhinoNativeFact]
    public void Plan_KerbCutToZeroHeight_BuildsOneSolidEachSide()
    {
        var toe = Enumerable.Range(0, 81).Select(i => new Point3d(6.0 + i * 0.1, 10, 0)).ToArray();
        var top = toe.Select(p => new Point3d(p.X, 10.05, KerbCutHeight(p.X))).ToArray();

        var plan = RetainingWallPlannerCore.Plan(new Curve[] { new PolylineCurve(toe), new PolylineCurve(top) }, 0.2);

        var wall = Assert.Single(plan.Walls);
        Assert.Equal(2, wall.Breps.Count);
        Assert.All(wall.Breps, AssertWatertight);
        Assert.True(wall.Breps[0].GetBoundingBox(true).Max.X < 9.4 + 1e-3);
        Assert.True(wall.Breps[1].GetBoundingBox(true).Min.X > 10.6 - 1e-3);
        Assert.DoesNotContain(plan.Report, entry => entry.Reason == RetainingWallPlannerCore.ReportReason.SolidFailed);
    }

    [RhinoNativeFact]
    public void Plan_KerbDrawnToPoint_BuildsSolidToTheTip()
    {
        var toe = Enumerable.Range(0, 81).Select(i => new Point3d(i * 0.1, 0, 0)).ToArray();
        var top = toe.Select(p => new Point3d(p.X, p.X < 7.5 ? 0.05 : 0.05 * (8.0 - p.X) / 0.5, 0.15)).ToArray();

        var plan = RetainingWallPlannerCore.Plan(new Curve[] { new PolylineCurve(toe), new PolylineCurve(top) }, 0.2);

        Brep brep = Assert.Single(Assert.Single(plan.Walls).Breps);
        AssertWatertight(brep);
        Assert.Equal(7.5 * 0.05 * 0.15 + 0.5 * 0.05 * 0.15 / 2.0, brep.GetVolume(), 4);
    }

    [RhinoNativeFact]
    public void Plan_KerbWithRoundNose_BuildsSolid()
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

        var plan = RetainingWallPlannerCore.Plan(new Curve[] { new PolylineCurve(toe.ToArray()), new PolylineCurve(top.ToArray()) }, 0.5);

        Brep brep = Assert.Single(Assert.Single(plan.Walls).Breps);
        AssertWatertight(brep);
        Assert.True(brep.GetBoundingBox(true).Max.X > 6.0 + r * 0.95);
    }

    private static double KerbCutHeight(double x)
    {
        double d = Math.Abs(x - 10.0);
        if (d <= 0.6)
            return 0.0;
        return d >= 1.2 ? 0.15 : 0.15 * (d - 0.6) / 0.6;
    }

    private static void AssertWatertight(Brep brep)
    {
        Assert.True(brep.IsValid);
        Assert.True(brep.IsSolid);
        Assert.All(brep.Edges, edge => Assert.NotEqual(EdgeAdjacency.Naked, edge.Valence));
    }

    [RhinoNativeFact]
    public void BrepBuilder_IndependentPolylineCounts_BuildsSolidViaSmartLoft()
    {
        Brep? brep = RetainingWallBrepBuilder.Build(
            new[]
            {
                new Point3d(0, 0, 0),
                new Point3d(8, 0, 1),
                new Point3d(16, 0, 0)
            },
            new[]
            {
                new Point3d(0, 1, 3),
                new Point3d(4, 1, 2),
                new Point3d(8, 1, 2),
                new Point3d(12, 1, 3),
                new Point3d(16, 1, 4)
            },
            0.1);

        Assert.NotNull(brep);
        Assert.True(brep!.IsSolid);
    }

    [RhinoNativeFact]
    public void BrepBuilder_EqualPolylineCounts_BuildsSolidBrep()
    {
        Brep? brep = RetainingWallBrepBuilder.Build(
            new[]
            {
                new Point3d(0, 0, 0),
                new Point3d(8, 0, 1),
                new Point3d(16, 0, 0)
            },
            new[]
            {
                new Point3d(0, 1, 3),
                new Point3d(8, 1, 2),
                new Point3d(16, 1, 4)
            },
            0.1);

        Assert.NotNull(brep);
        Assert.True(brep!.IsSolid);
    }

    // 3D Model 2028: joined rails whose corner climbs steeply on one rail only. Stationing by 3D length put
    // the steep rail's corner opposite a point partway along the other rail's leg, folding the wall.
    [RhinoNativeFact]
    public void BrepBuilder_JoinedCornersWithDifferentGrades_BuildsWatertightSolid()
    {
        Brep? brep = RetainingWallBrepBuilder.Build(
            new[] { new Point3d(0, 0, 0), new Point3d(10, 0, 0), new Point3d(10, 10, 0) },
            new[] { new Point3d(0, 1, 1), new Point3d(9, 1, 20), new Point3d(9, 10, 2) },
            .005);

        Assert.NotNull(brep);
        Assert.True(brep!.IsValid);
        Assert.True(brep.IsSolid);
        Assert.All(brep.Edges, edge => Assert.NotEqual(EdgeAdjacency.Naked, edge.Valence));
    }

    // Regression for the copied Terrain 1 retaining wall: two dense, steeply-climbing rails with
    // unequal point counts and a narrow (~0.25) footprint. The old per-segment box builder produced
    // hundreds of faces that JoinBreps fragmented into an invalid, non-solid Brep, so the wall solid
    // silently failed while breaklines were still inserted. The continuous-rail loft must weld into a
    // single watertight solid instead.
    [RhinoNativeFact]
    public void BrepBuilder_DenseSteepUnequalRails_BuildsWatertightSolid()
    {
        Point3d[] toe = ClimbingRail(count: 60, yOffset: 0.0, zBase: 0.0);
        Point3d[] top = ClimbingRail(count: 71, yOffset: 0.25, zBase: 2.0);

        Brep? brep = RetainingWallBrepBuilder.Build(toe, top, 0.01);

        Assert.NotNull(brep);
        Assert.True(brep!.IsValid);
        Assert.True(brep.IsSolid);
        Assert.All(brep.Edges, edge => Assert.NotEqual(EdgeAdjacency.Naked, edge.Valence));
    }

    // A rail that climbs ~23 units in Z along a gentle S-curve in XY, sampled at `count` stations.
    private static Point3d[] ClimbingRail(int count, double yOffset, double zBase)
    {
        var points = new Point3d[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);
            double x = t * 114.0;
            double y = yOffset + (Math.Sin(t * Math.PI) * 40.0);
            double z = zBase + (t * 23.0);
            points[i] = new Point3d(x, y, z);
        }

        return points;
    }

    private static Curve ClosedPolyline(params Point3d[] points)
    {
        var closed = new Point3d[points.Length + 1];
        Array.Copy(points, closed, points.Length);
        closed[^1] = points[0];
        return new PolylineCurve(closed);
    }
}
