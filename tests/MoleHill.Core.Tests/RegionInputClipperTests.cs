using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class RegionInputClipperTests
{
    private static readonly MeshAreaSplitter.AreaBoundary UnitSquare = new(
        new[] { 0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0 }, 4);

    [Fact]
    public void KeepPointsInside_IncludesBoundaryAndRejectsOutside()
    {
        double[] points = { 0.5, 0.5, 1.0, 1.0, 0.5, 2.0, 1.5, 0.5, 3.0 };

        bool[] keep = RegionInputClipper.KeepPointsInside(points, 3, new[] { UnitSquare }, 1e-9);

        Assert.Equal(new[] { true, true, false }, keep);
    }

    [Fact]
    public void ClipPolylines_CrossingBoundary_InterpolatesEndpointZ()
    {
        var input = new RegionInputClipper.InputPolyline(
            new[] { -1.0, 0.5, 0.0, 2.0, 0.5, 3.0 }, 2, false);

        RegionInputClipper.InputPolyline clipped = Assert.Single(
            RegionInputClipper.ClipPolylines(new[] { input }, new[] { UnitSquare }, 1e-9));

        Assert.Equal(0.0, clipped.Points[0], 9);
        Assert.Equal(1.0, clipped.Points[2], 9);
        Assert.Equal(1.0, clipped.Points[3], 9);
        Assert.Equal(2.0, clipped.Points[5], 9);
    }

    [Fact]
    public void ClipPolylines_DisjointUnion_KeepsBothParts()
    {
        var second = new MeshAreaSplitter.AreaBoundary(
            new[] { 2.0, 0.0, 3.0, 0.0, 3.0, 1.0, 2.0, 1.0 }, 4);
        var input = new RegionInputClipper.InputPolyline(
            new[] { -1.0, 0.5, 0.0, 4.0, 0.5, 5.0 }, 2, false);

        List<RegionInputClipper.InputPolyline> clipped = RegionInputClipper.ClipPolylines(
            new[] { input }, new[] { UnitSquare, second }, 1e-9);

        Assert.Equal(2, clipped.Count);
        Assert.Equal(0.0, clipped[0].Points[0], 9);
        Assert.Equal(1.0, clipped[0].Points[3], 9);
        Assert.Equal(2.0, clipped[1].Points[0], 9);
        Assert.Equal(3.0, clipped[1].Points[3], 9);
    }

    [Fact]
    public void ClipPolylines_PolylineOutsideEveryPolygonBox_KeepsNothing()
    {
        var input = new RegionInputClipper.InputPolyline(
            new[] { 5.0, 5.0, 0.0, 6.0, 5.0, 0.0, 6.0, 6.0, 0.0 }, 3, false);

        Assert.Empty(RegionInputClipper.ClipPolylines(new[] { input }, new[] { UnitSquare }, 1e-9));
    }

    /// <summary>
    /// The bounding-box reject must honour the tolerance band: a segment just outside the square, but
    /// within tolerance of its edge, is still kept, as the clipper's on-boundary test keeps it.
    /// </summary>
    [Fact]
    public void ClipPolylines_SegmentWithinToleranceOutsideEdge_IsKept()
    {
        var input = new RegionInputClipper.InputPolyline(
            new[] { 0.2, 1.005, 0.0, 0.8, 1.005, 0.0 }, 2, false);

        RegionInputClipper.InputPolyline clipped = Assert.Single(
            RegionInputClipper.ClipPolylines(new[] { input }, new[] { UnitSquare }, 0.01));

        Assert.Equal(0.2, clipped.Points[0], 9);
        Assert.Equal(0.8, clipped.Points[3], 9);
    }

    /// <summary>
    /// A far-away polygon rejected by its box must not change the pieces kept, including a run that
    /// re-enters the kept polygon over several segments.
    /// </summary>
    [Fact]
    public void ClipPolylines_FarPolygonInUnion_GivesSameResultAsWithoutIt()
    {
        var far = new MeshAreaSplitter.AreaBoundary(
            new[] { 100.0, 100.0, 101.0, 100.0, 101.0, 101.0, 100.0, 101.0 }, 4);
        var input = new RegionInputClipper.InputPolyline(
            new[] { -1.0, 0.5, 0.0, 0.5, 0.5, 1.0, 0.5, 3.0, 2.0, 0.7, 0.2, 3.0, 2.0, 0.2, 4.0 }, 5, false);

        List<RegionInputClipper.InputPolyline> withFar = RegionInputClipper.ClipPolylines(
            new[] { input }, new[] { UnitSquare, far }, 1e-9);
        List<RegionInputClipper.InputPolyline> without = RegionInputClipper.ClipPolylines(
            new[] { input }, new[] { UnitSquare }, 1e-9);

        Assert.Equal(without.Count, withFar.Count);
        for (int i = 0; i < without.Count; i++)
            Assert.Equal(without[i].Points, withFar[i].Points);
    }
}
