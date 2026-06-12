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
        Assert.NotNull(wall.Brep);
        Assert.True(wall.Brep!.IsSolid);
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
        Assert.NotNull(wall.Brep);
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
        Assert.NotNull(wall.Brep);
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
        Assert.NotNull(wall.Brep);
        Assert.True(wall.Brep!.IsSolid);
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
        Assert.NotNull(wall.Brep);
        Assert.True(wall.Brep!.IsSolid);
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

    private static Curve ClosedPolyline(params Point3d[] points)
    {
        var closed = new Point3d[points.Length + 1];
        Array.Copy(points, closed, points.Length);
        closed[^1] = points[0];
        return new PolylineCurve(closed);
    }
}
