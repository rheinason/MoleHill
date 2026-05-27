using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SectionLayoutHelperTests
{
    [Fact]
    public void ProjectToInsertionPlane_WorldXyIdentity_PlacesPointInPlane()
    {
        Point3d projected = SectionLayoutHelper.ProjectToInsertionPlane(
            Plane.WorldXY,
            station: 7.0,
            elevation: 12.0,
            horizontalScale: 1.0,
            verticalScale: 1.0,
            baseElevation: 0.0);

        Assert.Equal(7.0, projected.X, 6);
        Assert.Equal(12.0, projected.Y, 6);
        Assert.Equal(0.0, projected.Z, 6);
    }

    [Fact]
    public void ProjectToInsertionPlane_VerticalExaggeration_ScalesElevationOnly()
    {
        Point3d projected = SectionLayoutHelper.ProjectToInsertionPlane(
            Plane.WorldXY,
            station: 4.0,
            elevation: 10.0,
            horizontalScale: 1.0,
            verticalScale: 5.0,
            baseElevation: 2.0);

        Assert.Equal(4.0, projected.X, 6);
        Assert.Equal(40.0, projected.Y, 6);
    }

    [RhinoNativeFact]
    public void ProjectToInsertionPlane_TranslatedPlane_AppliesOriginOffset()
    {
        var plane = new Plane(new Point3d(100, 200, 50), Vector3d.XAxis, Vector3d.YAxis);

        Point3d projected = SectionLayoutHelper.ProjectToInsertionPlane(
            plane,
            station: 3.0,
            elevation: 8.0,
            horizontalScale: 1.0,
            verticalScale: 1.0,
            baseElevation: 0.0);

        Assert.Equal(103.0, projected.X, 6);
        Assert.Equal(208.0, projected.Y, 6);
        Assert.Equal(50.0, projected.Z, 6);
    }

    [RhinoNativeFact]
    public void ProjectToInsertionPlane_RotatedPlane_RespectsAxes()
    {
        var plane = new Plane(Point3d.Origin, Vector3d.YAxis, Vector3d.ZAxis);

        Point3d projected = SectionLayoutHelper.ProjectToInsertionPlane(
            plane,
            station: 4.0,
            elevation: 9.0,
            horizontalScale: 1.0,
            verticalScale: 1.0,
            baseElevation: 0.0);

        Assert.Equal(0.0, projected.X, 6);
        Assert.Equal(4.0, projected.Y, 6);
        Assert.Equal(9.0, projected.Z, 6);
    }

    [Fact]
    public void LayoutFlat_ProducesPolylineMatchingProjection()
    {
        var segment = new TerrainSectionSegment(new[]
        {
            new TerrainSectionVertex(0.0, new Point3d(0, 0, 5)),
            new TerrainSectionVertex(5.0, new Point3d(5, 0, 7)),
            new TerrainSectionVertex(10.0, new Point3d(10, 0, 11))
        });

        Polyline polyline = SectionLayoutHelper.LayoutFlat(
            segment,
            Plane.WorldXY,
            horizontalScale: 1.0,
            verticalScale: 1.0,
            baseElevation: 5.0);

        Assert.Equal(3, polyline.Count);
        Assert.Equal(0.0, polyline[0].X, 6);
        Assert.Equal(0.0, polyline[0].Y, 6);
        Assert.Equal(5.0, polyline[1].X, 6);
        Assert.Equal(2.0, polyline[1].Y, 6);
        Assert.Equal(10.0, polyline[2].X, 6);
        Assert.Equal(6.0, polyline[2].Y, 6);
    }

    [Fact]
    public void BuildElevationGridLines_RoundsToGridSpacing()
    {
        var lines = SectionLayoutHelper.BuildElevationGridLines(
            Plane.WorldXY,
            totalStation: 10.0,
            minimumElevation: 2.3,
            maximumElevation: 5.6,
            baseElevation: 0.0,
            gridSpacing: 1.0,
            horizontalScale: 1.0,
            verticalScale: 1.0);

        Assert.Equal(5, lines.Count);
        Assert.Equal(2.0, lines[0].FromY, 6);
        Assert.Equal(6.0, lines[lines.Count - 1].FromY, 6);
        foreach (var line in lines)
        {
            Assert.Equal(0.0, line.FromX, 6);
            Assert.Equal(10.0, line.ToX, 6);
        }
    }

    [Fact]
    public void BuildElevationGridLines_ZeroSpacing_ReturnsEmpty()
    {
        var lines = SectionLayoutHelper.BuildElevationGridLines(
            Plane.WorldXY,
            totalStation: 10.0,
            minimumElevation: 0.0,
            maximumElevation: 5.0,
            baseElevation: 0.0,
            gridSpacing: 0.0,
            horizontalScale: 1.0,
            verticalScale: 1.0);

        Assert.Empty(lines);
    }

    [Fact]
    public void BuildStationTicks_ReturnsTickPerStation()
    {
        var stations = new[] { 0.0, 5.0, 10.0 };

        var ticks = SectionLayoutHelper.BuildStationTicks(
            Plane.WorldXY,
            stations,
            tickHalfHeight: 0.5,
            horizontalScale: 1.0,
            verticalScale: 1.0,
            baseElevation: 0.0,
            tickElevation: 0.0);

        Assert.Equal(3, ticks.Count);
        Assert.Equal(0.0, ticks[0].FromX, 6);
        Assert.Equal(-0.5, ticks[0].FromY, 6);
        Assert.Equal(0.5, ticks[0].ToY, 6);
        Assert.Equal(5.0, ticks[1].FromX, 6);
        Assert.Equal(10.0, ticks[2].FromX, 6);
    }

    [Fact]
    public void BuildBaselineAxis_SpansFromZeroToTotalStation()
    {
        Line baseline = SectionLayoutHelper.BuildBaselineAxis(
            Plane.WorldXY,
            totalStation: 25.0,
            horizontalScale: 1.0,
            verticalScale: 1.0,
            axisElevation: 0.0,
            baseElevation: 0.0);

        Assert.Equal(0.0, baseline.FromX, 6);
        Assert.Equal(25.0, baseline.ToX, 6);
        Assert.Equal(0.0, baseline.FromY, 6);
        Assert.Equal(0.0, baseline.ToY, 6);
    }

    [RhinoNativeFact]
    public void FrameFromCurveTangent_FlattensZComponent()
    {
        var tangent = new Vector3d(1.0, 0.0, 0.5);
        Plane frame = SectionLayoutHelper.FrameFromCurveTangent(Point3d.Origin, tangent);

        Assert.Equal(0.0, frame.XAxis.Z, 6);
        Assert.Equal(1.0, frame.XAxis.Length, 6);
        Assert.Equal(0.0, frame.YAxis.Z, 6);
        Assert.True(Vector3d.CrossProduct(frame.XAxis, frame.YAxis).Z > 0.99);
    }

    [RhinoNativeFact]
    public void FrameFromCurveTangent_ZeroXyTangent_FallsBackToWorldX()
    {
        var tangent = new Vector3d(0.0, 0.0, 1.0);
        Plane frame = SectionLayoutHelper.FrameFromCurveTangent(Point3d.Origin, tangent);

        Assert.Equal(1.0, frame.XAxis.X, 6);
        Assert.Equal(0.0, frame.XAxis.Y, 6);
    }
}
