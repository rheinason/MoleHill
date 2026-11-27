using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ProfileSnapTests
{
    private static readonly SlopeAnalyzer.SlopeUnit Percent = SlopeAnalyzer.SlopeUnit.Percent;

    [Fact]
    public void Snap_OutOfReach_KeepsTheCursor()
    {
        ProfileSnapResult result = ProfileSnap.Snap(50.0, 13.3712, 1e-4, new ProfileBend(0, 10, 0), null, null, Percent);

        Assert.Equal(ProfileSnapKind.None, result.Kind);
        Assert.Equal(13.3712, result.Elevation);
    }

    [Fact]
    public void Snap_Ground_WinsWhenNearest()
    {
        ProfileSnapResult result = ProfileSnap.Snap(50.0, 12.31, 0.05, new ProfileBend(0, 10, 0), null, ground: 12.30, Percent);

        Assert.Equal(ProfileSnapKind.Ground, result.Kind);
        Assert.Equal(12.30, result.Elevation, 12);
    }

    [Fact]
    public void Snap_LevelWithANeighbour()
    {
        ProfileSnapResult result = ProfileSnap.Snap(50.0, 10.003, 0.01, null, new ProfileBend(80, 10, 1), null, Percent);

        Assert.Equal(ProfileSnapKind.Level, result.Kind);
        Assert.True(result.FromNext);
        Assert.Equal(10.0, result.Elevation, 12);
    }

    [Fact]
    public void Snap_RoundGradeFromThePreviousBend()
    {
        // 2.04% from the bend at station 0: rounds to 2.0%, an elevation 0.02 lower at 50 m.
        ProfileSnapResult result = ProfileSnap.Snap(50.0, 11.02, 0.05, new ProfileBend(0, 10, 0), null, null, Percent);

        Assert.Equal(ProfileSnapKind.Grade, result.Kind);
        Assert.Equal(0.02, result.Grade, 12);
        Assert.Equal(11.0, result.Elevation, 12);
    }

    [Theory]
    [InlineData(0.0512, SlopeAnalyzer.SlopeUnit.Percent, 0.05)]
    [InlineData(0.0512, SlopeAnalyzer.SlopeUnit.Promille, 0.05)]
    [InlineData(0.0490, SlopeAnalyzer.SlopeUnit.Ratio, 0.05)] // 1:20.4 -> 1:20
    [InlineData(-0.0490, SlopeAnalyzer.SlopeUnit.Ratio, -0.05)]
    public void RoundGrade_UsesTheUsersUnit(double grade, SlopeAnalyzer.SlopeUnit unit, double expected)
    {
        Assert.Equal(expected, ProfileSnap.RoundGrade(grade, unit), 9);
    }

    [Fact]
    public void RoundGrade_Degrees_RoundsToHalfDegrees()
    {
        double rounded = ProfileSnap.RoundGrade(Math.Tan(10.2 * Math.PI / 180.0), SlopeAnalyzer.SlopeUnit.Degrees);

        Assert.Equal(10.0, Math.Atan(rounded) * 180.0 / Math.PI, 9);
    }

    [Fact]
    public void GroundLine_IsNullOffTheGround()
    {
        var ground = new ProfileGroundLine(new[] { new double[] { 0, 1, 10, 2 }, new double[] { 20, 5, 30, 5 } });

        Assert.Equal(1.5, ground.ElevationAt(5.0)!.Value, 12);
        Assert.Null(ground.ElevationAt(15.0));
        Assert.Equal(5.0, ground.ElevationAt(25.0)!.Value, 12);
        Assert.Equal((1.0, 5.0), ground.Extent());
    }
}
