using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class GeometryCommandAlgorithmsTests
{
    [Fact]
    public void InterpolateTwoPointElevation_Midpoint_ReturnsAverageElevation()
    {
        Point3d low = new(0, 0, 10);
        Point3d high = new(10, 0, 20);
        Point3d sample = new(5, 0, 999);

        double elevation = GeometryCommandAlgorithms.InterpolateTwoPointElevation(low, high, sample);

        Assert.Equal(15.0, elevation, 6);
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
}
