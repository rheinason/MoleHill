using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class CoordinatePrecisionTests
{
    [Theory]
    [InlineData(250.0, 0.01, false)]        // a local site, like the Glyvra terrain
    [InlineData(5_000.0, 0.01, false)]      // a large local site
    [InlineData(500_000.0, 0.01, true)]     // UTM easting
    [InlineData(6_000_000.0, 0.01, true)]   // UTM northing
    [InlineData(20_000.0, 0.1, false)]      // a coarse tolerance tolerates more distance
    public void IsTooFarFromOrigin_WarnsOnlyWhereSinglePrecisionApproachesTheTolerance(double magnitude, double tolerance, bool expected)
    {
        Assert.Equal(expected, CoordinatePrecision.IsTooFarFromOrigin(magnitude, tolerance));
    }

    [Fact]
    public void SafeDistance_IsWhereTheFloat32StepReachesATenthOfTheTolerance()
    {
        double safe = CoordinatePrecision.SafeDistance(0.01);

        Assert.InRange(safe, 8_000.0, 9_000.0);
        Assert.Equal(0.001, CoordinatePrecision.RoundingStep(safe), 9);
    }

    [Fact]
    public void RoundingStep_AtAUtmNorthing_IsWellOverHalfAMetre()
    {
        Assert.InRange(CoordinatePrecision.RoundingStep(6_000_000.0), 0.7, 0.72);
    }

    [Fact]
    public void MaxPlanMagnitude_IgnoresNonFiniteValues()
    {
        double[] xy = [1.0, -700.0, double.NaN, 3.0, double.PositiveInfinity, 2.0];

        Assert.Equal(700.0, CoordinatePrecision.MaxPlanMagnitude(xy, 3));
    }
}
