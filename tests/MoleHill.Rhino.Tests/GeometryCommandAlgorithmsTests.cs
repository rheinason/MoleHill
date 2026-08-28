using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class GeometryCommandAlgorithmsTests
{
    [RhinoNativeFact]
    public void CalculatePlanLength_IgnoresCurveElevation()
    {
        var curve = new LineCurve(new Point3d(0, 0, 10), new Point3d(3, 4, 110));

        double length = GeometryCommandAlgorithms.CalculatePlanLength(curve, curve.Domain.T0, curve.Domain.T1);

        Assert.Equal(5.0, length, 6);
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_ZeroGrade_FlattensSelectedLine()
    {
        var curve = new LineCurve(new Point3d(0, 0, 10), new Point3d(10, 0, 20));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.T0, curve.Domain.T1, CurveSectionEditMode.GradePercent,
            0.0, 0.0, null, 1e-6, out Curve? result, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        Assert.Equal(10.0, result!.PointAtStart.Z, 6);
        Assert.Equal(10.0, result.PointAtEnd.Z, 6);
        Assert.Equal(10.0, result.PointAtEnd.X, 6);
        result.Dispose();
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_InteriorZeroGrade_FlattensOnlySelectedInterval()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 10), new Point3d(10, 0, 20));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.75),
            CurveSectionEditMode.GradePercent, 0.0, 0.0, null, 1e-6,
            out Curve? result, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            AssertCurveContainsPoint(result!, new Point3d(1, 0, 11));
            AssertCurveContainsPoint(result, new Point3d(5, 0, 12.5));
            AssertCurveContainsPoint(result, new Point3d(9, 0, 19));
            AssertCurveContainsPoint(result, new Point3d(7.5, 0, 12.5));
            AssertCurveContainsPoint(result, new Point3d(7.5, 0, 17.5));
        }
    }
    [Fact]
    public void InterpolateTwoPointElevation_Midpoint_ReturnsAverageElevation()
    {
        Point3d low = new(0, 0, 10);
        Point3d high = new(10, 0, 20);
        Point3d sample = new(5, 0, 999);

        double elevation = GeometryCommandAlgorithms.InterpolateTwoPointElevation(low, high, sample);

        Assert.Equal(15.0, elevation, 6);
    }

    private static void AssertCurveContainsPoint(Curve curve, Point3d expected)
    {
        Assert.True(curve.ClosestPoint(expected, out double parameter));
        Assert.True(curve.PointAt(parameter).EpsilonEquals(expected, 1e-6),
            $"Expected curve to contain {expected}, closest point was {curve.PointAt(parameter)}.");
    }

    [Fact]
    public void InterpolateGradientElevation_UsesPromilleAndPlanDistance()
    {
        Point3d basePoint = new(0, 0, 100);
        Point3d sample = new(30, 40, 0);

        double elevation = GeometryCommandAlgorithms.InterpolateGradientElevation(basePoint, sample, 100.0);

        Assert.Equal(105.0, elevation, 6);
    }

    [Fact]
    public void CalculateSoftEditPoints_UsesRadiusBasedFalloff()
    {
        var points = new[]
        {
            new Point3d(-10, 0, 0),
            new Point3d(0, 0, 0),
            new Point3d(5, 0, 0),
            new Point3d(10, 0, 0)
        };

        List<Point3d> adjusted = GeometryCommandAlgorithms.CalculateSoftEditPoints(
            points,
            Point3d.Origin,
            10.0,
            new Vector3d(0, 0, 10));

        Assert.Equal(0.0, adjusted[0].Z, 6);
        Assert.Equal(10.0, adjusted[1].Z, 6);
        Assert.InRange(adjusted[2].Z, 4.9, 5.1);
        Assert.Equal(0.0, adjusted[3].Z, 6);
    }

    [Fact]
    public void CalculateSoftEditPoints_LinearFalloff_DoesNotApplySineEasing()
    {
        var points = new[] { new Point3d(2.5, 0, 0) };

        List<Point3d> adjusted = GeometryCommandAlgorithms.CalculateSoftEditPoints(
            points,
            Point3d.Origin,
            10.0,
            new Vector3d(0, 0, 10),
            SoftEditFalloff.Linear);

        Assert.Equal(7.5, adjusted[0].Z, 6);
    }

    [Fact]
    public void CalculatePlanDistance_IgnoresElevationDifference()
    {
        double distance = GeometryCommandAlgorithms.CalculatePlanDistance(
            new Point3d(1, 2, -100),
            new Point3d(4, 6, 500));

        Assert.Equal(5.0, distance, 6);
    }

    [Fact]
    public void BoundingBoxIntersectsPlanRadius_UsesNearestPlanDistance()
    {
        var bounds = new BoundingBox(
            new Point3d(0, 0, 100),
            new Point3d(10, 10, 200));

        Assert.True(GeometryCommandAlgorithms.BoundingBoxIntersectsPlanRadius(
            bounds,
            new Point3d(15, 5, -999),
            6.0));
        Assert.False(GeometryCommandAlgorithms.BoundingBoxIntersectsPlanRadius(
            bounds,
            new Point3d(20, 5, 150),
            6.0));
    }

    [RhinoNativeFact]
    public void TryCreateSoftEditedCurve_NurbsInput_RemainsNonPolylineAndLeavesSourceUntouched()
    {
        NurbsCurve source = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(-10, 0, 0),
                new Point3d(-3, 0, 0),
                new Point3d(3, 0, 0),
                new Point3d(10, 0, 0)
            });

        bool succeeded = GeometryCommandAlgorithms.TryCreateSoftEditedCurve(
            source,
            Point3d.Origin,
            10.0,
            new Vector3d(0, 0, 10),
            SoftEditFalloff.Smooth,
            fixEnds: false,
            tolerance: 0.001,
            quickPreview: false,
            out Curve? result,
            out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        Assert.IsNotType<PolylineCurve>(result);
        Assert.True(result!.GetBoundingBox(accurate: true).Max.Z > 5.0);
        Assert.Equal(0.0, source.GetBoundingBox(accurate: true).Max.Z, 6);
        result.Dispose();
    }

    [RhinoNativeFact]
    public void TryCreateSoftEditedCurve_FixEnds_PreservesOpenCurveEndpoints()
    {
        NurbsCurve source = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(-10, 0, 0),
                new Point3d(-3, 0, 0),
                new Point3d(3, 0, 0),
                new Point3d(10, 0, 0)
            });

        bool succeeded = GeometryCommandAlgorithms.TryCreateSoftEditedCurve(
            source,
            source.PointAtStart,
            15.0,
            new Vector3d(0, 0, 10),
            SoftEditFalloff.Smooth,
            fixEnds: true,
            tolerance: 0.001,
            quickPreview: false,
            out Curve? result,
            out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        Assert.True(result!.PointAtStart.EpsilonEquals(source.PointAtStart, 1e-9));
        Assert.True(result.PointAtEnd.EpsilonEquals(source.PointAtEnd, 1e-9));
        Assert.True(result.GetBoundingBox(accurate: true).Max.Z > 0.0);
        result.Dispose();
    }

    [Fact]
    public void CalculateSlopePercentMagnitude_UsesHorizontalRun()
    {
        double percent = GeometryCommandAlgorithms.CalculateSlopePercentMagnitude(new Vector3d(4, 3, 5));

        Assert.Equal(100.0, percent, 6);
    }

    [Fact]
    public void CalculateDefaultLiftFactor_RespectsKnownUnits()
    {
        Assert.Equal(2000.0, GeometryCommandAlgorithms.CalculateDefaultLiftFactor(global::Rhino.UnitSystem.Millimeters));
        Assert.Equal(2.0, GeometryCommandAlgorithms.CalculateDefaultLiftFactor(global::Rhino.UnitSystem.Meters));
        Assert.Equal(6.56167979002625, GeometryCommandAlgorithms.CalculateDefaultLiftFactor(global::Rhino.UnitSystem.Feet), 12);
    }

    [Fact]
    public void ShouldUseSlopePercentageInput_FlatCurve_ReturnsTrue()
    {
        bool usePercentage = GeometryCommandAlgorithms.ShouldUseSlopePercentageInput(100.0, 100.0004, 0.001);

        Assert.True(usePercentage);
    }

    [Fact]
    public void ShouldUseSlopePercentageInput_SlopedCurve_ReturnsFalse()
    {
        bool usePercentage = GeometryCommandAlgorithms.ShouldUseSlopePercentageInput(100.0, 100.01, 0.001);

        Assert.False(usePercentage);
    }

    [Fact]
    public void GetSignedOffsetDistanceForSide_LeftSide_ReturnsNegativeDistance()
    {
        double signedDistance = GeometryCommandAlgorithms.GetSignedOffsetDistanceForSide(true, 2.0);

        Assert.Equal(-2.0, signedDistance, 6);
    }

    [Fact]
    public void GetSignedOffsetDistanceForSide_RightSide_ReturnsPositiveDistance()
    {
        double signedDistance = GeometryCommandAlgorithms.GetSignedOffsetDistanceForSide(false, 2.0);

        Assert.Equal(2.0, signedDistance, 6);
    }

    [Fact]
    public void EaseInOutSine_MapsUnitIntervalSmoothly()
    {
        Assert.Equal(0.0, GeometryCommandAlgorithms.EaseInOutSine(0.0), 6);
        Assert.Equal(0.5, GeometryCommandAlgorithms.EaseInOutSine(0.5), 6);
        Assert.Equal(1.0, GeometryCommandAlgorithms.EaseInOutSine(1.0), 6);
    }

    [Fact]
    public void TryResolveVerticalDelta_Elevation_IgnoresHorizontalDistance()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Elevation,
            -0.04,
            2.0,
            out double verticalDelta,
            out string? error);

        Assert.True(resolved);
        Assert.Null(error);
        Assert.Equal(-0.04, verticalDelta, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_Degrees45_ReturnsHorizontalDistance()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Degrees,
            -45.0,
            5.0,
            out double verticalDelta,
            out _);

        Assert.True(resolved);
        Assert.Equal(-5.0, verticalDelta, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_DegreesAtVertical_ReturnsError()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Degrees,
            90.0,
            5.0,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolveVerticalDelta_Ratio_OneInTen_ReturnsTenthOfDistance()
    {
        Assert.True(GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio, 10.0, 5.0, out double rising, out _));
        Assert.Equal(0.5, rising, 9);

        Assert.True(GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio, -10.0, 5.0, out double falling, out _));
        Assert.Equal(-0.5, falling, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_ZeroRatio_ReturnsError()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio,
            0.0,
            5.0,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolveVerticalDelta_Percent_MatchesRatio()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Percent,
            -4.0,
            2.0,
            out double verticalDelta,
            out _);

        Assert.True(resolved);
        Assert.Equal(-0.08, verticalDelta, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_UsesOffsetDistanceNotSignedDistance()
    {
        // The offset side must not flip the vertical: a left offset drops just like a right one.
        Assert.True(GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio, -10.0, -5.0, out double leftSide, out _));

        Assert.Equal(-0.5, leftSide, 9);
    }

    [RhinoNativeFact]
    public void TryGetOffsetFeaturePolyline_AppliesVerticalDeltaToEveryVertex()
    {
        // A sloped, kinked feature line so the source's own longitudinal grade is exercised.
        var source = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 10),
            new Point3d(10, 0, 11),
            new Point3d(20, 10, 12)
        });

        bool offsetSucceeded = GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline(
            source,
            2.0,
            -0.04,
            0.001,
            out Polyline offset,
            out string? error);

        Assert.True(offsetSucceeded, error);
        Assert.True(offset.Count >= 2);

        Curve flat = Curve.ProjectToPlane(source, Plane.WorldXY);
        foreach (Point3d point in offset)
        {
            Assert.True(flat.ClosestPoint(point, out double parameter));
            Assert.Equal(source.PointAt(parameter).Z - 0.04, point.Z, 6);
        }

        source.Dispose();
    }

    [RhinoNativeFact]
    public void TryGetOffsetFeaturePolyline_ZeroVerticalDelta_PreservesSourceElevations()
    {
        var source = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 5),
            new Point3d(10, 0, 7)
        });

        Assert.True(GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline(
            source, 3.0, 0.0, 0.001, out Polyline offset, out string? error), error);

        Assert.Equal(5.0, offset[0].Z, 6);
        Assert.Equal(7.0, offset[^1].Z, 6);

        source.Dispose();
    }
}
