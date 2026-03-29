using MoleHill.Grasshopper.Grading;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class RetainingWallPlannerGeometryTests
{
    [Fact]
    public void Plan_ZeroHeightStart_TapersToLineEdge()
    {
        var plan = RetainingWallPlanner.Plan(
            new Curve[]
            {
                new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
                new LineCurve(new Point3d(0, 1, 0), new Point3d(10, 1, 4))
            },
            1.0);

        var wall = Assert.Single(plan.Walls);
        Brep brep = Assert.IsType<Brep>(wall.Brep);

        var startVerts = DistinctVerticesWhere(brep, p => Math.Abs(p.X) <= 1e-6);
        Assert.Equal(2, startVerts.Count);
        Assert.Contains((0.0, 0.0, 0.0), startVerts);
        Assert.Contains((0.0, 1.0, 0.0), startVerts);
    }

    [Fact]
    public void Plan_ZeroHeightEnd_TapersToLineEdge()
    {
        var plan = RetainingWallPlanner.Plan(
            new Curve[]
            {
                new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0)),
                new LineCurve(new Point3d(0, 1, 4), new Point3d(10, 1, 0))
            },
            1.0);

        var wall = Assert.Single(plan.Walls);
        Brep brep = Assert.IsType<Brep>(wall.Brep);

        var endVerts = DistinctVerticesWhere(brep, p => Math.Abs(p.X - 10.0) <= 1e-6);
        Assert.Equal(2, endVerts.Count);
        Assert.Contains((10.0, 0.0, 0.0), endVerts);
        Assert.Contains((10.0, 1.0, 0.0), endVerts);
    }

    [Fact]
    public void Plan_TwoWallCorner_PreservesWallWidthAtResolvedMiter()
    {
        var plan = RetainingWallPlanner.Plan(
            new Curve[]
            {
                new LineCurve(new Point3d(-5, 0, 0), new Point3d(0, 0, 0)),
                new LineCurve(new Point3d(-5, 1, 3), new Point3d(0, 1, 3)),
                new LineCurve(new Point3d(0, 0, 0), new Point3d(0, 5, 0)),
                new LineCurve(new Point3d(-1, 0, 3), new Point3d(-1, 5, 3))
            },
            1.0);

        Assert.Equal(2, plan.Walls.Count);

        var firstWall = Assert.Single(plan.Walls, w => MatchesPair(w, 0, 1));
        var secondWall = Assert.Single(plan.Walls, w => MatchesPair(w, 2, 3));

        Assert.NotNull(firstWall.Brep);
        Assert.NotNull(secondWall.Brep);

        int firstLast = firstWall.Strip.StationCount - 1;
        Assert.Equal(0.0, firstWall.Strip.ToeXy[firstLast * 2], 6);
        Assert.Equal(0.0, firstWall.Strip.ToeXy[firstLast * 2 + 1], 6);
        Assert.Equal(-1.0, firstWall.Strip.TopXy[firstLast * 2], 6);
        Assert.Equal(1.0, firstWall.Strip.TopXy[firstLast * 2 + 1], 6);

        Assert.Equal(0.0, secondWall.Strip.ToeXy[0], 6);
        Assert.Equal(0.0, secondWall.Strip.ToeXy[1], 6);
        Assert.Equal(-1.0, secondWall.Strip.TopXy[0], 6);
        Assert.Equal(1.0, secondWall.Strip.TopXy[1], 6);
    }

    private static bool MatchesPair(RetainingWallPlanner.PlannedWall wall, int a, int b) =>
        wall.CurveA == a && wall.CurveB == b;

    private static List<(double X, double Y, double Z)> DistinctVerticesWhere(Brep brep, Func<Point3d, bool> predicate)
    {
        return brep.Vertices
            .Select(vertex => vertex.Location)
            .Where(predicate)
            .Select(RoundPoint)
            .Distinct()
            .ToList();
    }

    private static (double X, double Y, double Z) RoundPoint(Point3d point) =>
        (Math.Round(point.X, 6), Math.Round(point.Y, 6), Math.Round(point.Z, 6));
}
