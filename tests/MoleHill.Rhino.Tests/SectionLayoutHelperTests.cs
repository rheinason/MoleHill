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

    [Theory]
    [InlineData(0.0, 4.0, 0.0, 1.0)]
    [InlineData(100.0, 112.0, 0.0, 5.0)]
    [InlineData(0.0, 4.0, 0.25, 0.25)]
    [InlineData(20.0, 20.0, 0.0, 1.0)]      // a level pad still gets a step, so its elevation is labelled
    [InlineData(152.3, 152.3, 0.0, 10.0)]
    [InlineData(0.0, 0.0, 0.0, 1.0)]
    public void ResolveElevationGridSpacing_AutoUsesReadableStep(
        double minimum,
        double maximum,
        double requested,
        double expected)
    {
        Assert.Equal(expected, SectionLayoutHelper.ResolveElevationGridSpacing(minimum, maximum, requested));
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

    [Fact]
    public void ElevationSteps_AreWholeMultiplesSpanningTheRange()
    {
        IReadOnlyList<double> steps = SectionLayoutHelper.ElevationSteps(12.34, 12.81, 0.1);

        Assert.Equal(new[] { 12.3, 12.4, 12.5, 12.6, 12.7, 12.8, 12.9 }, steps);
    }

    [Fact]
    public void ElevationSteps_AbsurdlyFineSpacing_DrawsNothing()
    {
        Assert.Empty(SectionLayoutHelper.ElevationSteps(0.0, 1000.0, 0.001));
    }

    [Theory]
    [InlineData(5.0, "F0")]
    [InlineData(1.0, "F0")]
    [InlineData(0.5, "F1")]
    [InlineData(0.25, "F2")]
    [InlineData(0.1, "F1")]
    [InlineData(0.0001, "F3")]
    public void ElevationLabelFormat_ShowsEveryStepExactly(double spacing, string expected)
    {
        Assert.Equal(expected, SectionLayoutHelper.ElevationLabelFormat(spacing));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void SectionMark_RunsThroughTheAlphabetLikeSpreadsheetColumns(int index, string expected)
    {
        Assert.Equal(expected, TerrainAnalysisAnnotationBuilder.SectionMark(index));
    }

    [Theory]
    [InlineData(5.0, 1.0, 1)]   // steps far apart: label every one
    [InlineData(1.0, 1.0, 2)]   // 1 m steps, 1 m text: every 2nd
    [InlineData(0.5, 1.0, 5)]
    [InlineData(0.1, 1.0, 20)]
    public void ElevationLabelStride_KeepsFiguresClearOfEachOther(double drawnStep, double textHeight, int expected)
    {
        Assert.Equal(expected, SectionLayoutHelper.ElevationLabelStride(drawnStep, textHeight));
    }
}
