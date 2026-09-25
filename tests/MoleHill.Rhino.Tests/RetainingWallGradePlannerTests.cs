using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The outward normals the wall planner hands the grader decide which way each rail batters. The two
/// rails are tessellated independently, so these cover the pairings an index-for-index match got wrong.
/// Point3d is a managed struct, so these run without the native runtime.
/// </summary>
public class RetainingWallGradePlannerTests
{
    [Fact]
    public void BuildOutwardNormals_RailsWithDifferentVertexCounts_PointAwayFromPartner()
    {
        // Toe along y=0 with 11 stations; top along y=1 with only 3. By index, toe station 5 would
        // pair with the top rail's end at (10, 1) and point diagonally.
        var toe = new Point3d[11];
        for (int i = 0; i < toe.Length; i++)
            toe[i] = new Point3d(i, 0.0, 0.0);
        var top = new[] { new Point3d(0, 1, 3), new Point3d(5, 1, 3), new Point3d(10, 1, 3) };

        double[] toeNormals = RetainingWallGradePlanner.BuildOutwardNormals(toe, top, 1e-6);
        double[] topNormals = RetainingWallGradePlanner.BuildOutwardNormals(top, toe, 1e-6);

        for (int i = 0; i < toe.Length; i++)
        {
            Assert.Equal(0.0, toeNormals[i * 2], 9);
            Assert.Equal(-1.0, toeNormals[(i * 2) + 1], 9);
        }

        for (int i = 0; i < top.Length; i++)
        {
            Assert.Equal(0.0, topNormals[i * 2], 9);
            Assert.Equal(1.0, topNormals[(i * 2) + 1], 9);
        }
    }

    [Fact]
    public void BuildOutwardNormals_ClosedRingsWithDifferentStartPoints_PointAwayFromPartner()
    {
        // Two concentric squares: the inner (toe) ring starts at its bottom-left corner, the outer
        // (top) ring at its top-right corner. By index every station pairs with the opposite side.
        var inner = new[]
        {
            new Point3d(-5, -5, 0), new Point3d(5, -5, 0), new Point3d(5, 5, 0), new Point3d(-5, 5, 0),
        };
        var outer = new[]
        {
            new Point3d(6, 6, 3), new Point3d(-6, 6, 3), new Point3d(-6, -6, 3), new Point3d(6, -6, 3),
        };

        double[] innerNormals = RetainingWallGradePlanner.BuildOutwardNormals(inner, outer, 1e-6, partnerClosed: true);
        double[] outerNormals = RetainingWallGradePlanner.BuildOutwardNormals(outer, inner, 1e-6, partnerClosed: true);

        // Inner ring batters inward (toward the origin), outer ring outward (away from it).
        for (int i = 0; i < inner.Length; i++)
        {
            double dot = (innerNormals[i * 2] * inner[i].X) + (innerNormals[(i * 2) + 1] * inner[i].Y);
            Assert.True(dot < 0.0, $"Inner station {i} points toward the wall.");
        }

        for (int i = 0; i < outer.Length; i++)
        {
            double dot = (outerNormals[i * 2] * outer[i].X) + (outerNormals[(i * 2) + 1] * outer[i].Y);
            Assert.True(dot > 0.0, $"Outer station {i} points toward the wall.");
        }
    }

    [Fact]
    public void Build_SideOverride_AppliesToWhicheverSideTheRailGrades()
    {
        // Whether a rail batters to the grader's left or right depends on its direction, so a side's
        // override must reach both — otherwise a right-facing rail silently grades at the shared pair.
        var toe = new[] { new Point3d(0, 0, 0), new Point3d(10, 0, 0) };
        var top = new[] { new Point3d(0, 1, 3), new Point3d(10, 1, 3) };
        var wall = new RetainingWallPlannerCore.PlannedWall(
            new RetainingWallPlannerCore.WallRails(toe, top, isClosed: false, minWidth: 1.0),
            brep: null,
            curveA: 0,
            curveB: 1,
            pairLine: new Line(toe[0], top[0]));

        var grades = RetainingWallGradePlanner.Build(
            new[] { wall },
            new RetainingWallGradePlanner.Options
            {
                FillAngleDeg = 33.0,
                Toe = new RetainingWallGradePlanner.SideSlopes(20.0, 25.0),
                Top = new RetainingWallGradePlanner.SideSlopes(40.0, 45.0),
            });

        Assert.Equal(2, grades.Count);
        Assert.Equal(20.0, grades[0].LeftCutSlopeAngleDeg, 9);
        Assert.Equal(20.0, grades[0].RightCutSlopeAngleDeg, 9);
        Assert.Equal(25.0, grades[0].LeftFillSlopeAngleDeg, 9);
        Assert.Equal(25.0, grades[0].RightFillSlopeAngleDeg, 9);
        Assert.Equal(40.0, grades[1].LeftCutSlopeAngleDeg, 9);
        Assert.Equal(40.0, grades[1].RightCutSlopeAngleDeg, 9);
        Assert.Equal(45.0, grades[1].LeftFillSlopeAngleDeg, 9);
        Assert.Equal(45.0, grades[1].RightFillSlopeAngleDeg, 9);
    }

    [Fact]
    public void BuildOutwardNormals_ClosedPartnerNearestTheClosingSegment_UsesThatSegment()
    {
        // A station beside the partner's closing segment (last vertex back to first) must measure
        // across to that segment, not to the nearer of its two end vertices.
        var partner = new[]
        {
            new Point3d(0, 0, 0), new Point3d(10, 0, 0), new Point3d(10, 10, 0), new Point3d(0, 10, 0),
        };
        var rail = new[] { new Point3d(-1, 2, 3), new Point3d(-1, 8, 3) };

        double[] normals = RetainingWallGradePlanner.BuildOutwardNormals(rail, partner, 1e-6, partnerClosed: true);

        Assert.Equal(-1.0, normals[0], 9);
        Assert.Equal(0.0, normals[1], 9);
        Assert.Equal(-1.0, normals[2], 9);
        Assert.Equal(0.0, normals[3], 9);
    }
}
