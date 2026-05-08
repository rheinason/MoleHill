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
