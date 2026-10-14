using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class GradingEnvelopeTests
{
    // Flat ground at z = 0 over [0, 10] x [0, 10], split along its diagonal.
    private static readonly IndexedTriMesh FlatGround = new(
        new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0, 10.0, 10.0, 0.0, 0.0, 10.0, 0.0 },
        4,
        new[] { 0, 1, 2, 0, 2, 3 },
        2);

    private static readonly PathGrader.PathDefinition RoadAtOne = new(
        xyVertices: new[] { 0.0, 5.0, 10.0, 5.0 },
        zValues: new[] { 1.0, 1.0 },
        vertexCount: 2,
        width: 2.0);

    [Fact]
    public void FindForPaths_PointAboveDesignAndGround_IsViolation()
    {
        // 3 m from the road (1.0) over ground at 0: a batter point lies in [0, 1]; 2.5 is a spike.
        double[] vertices = { 5.0, 8.0, 2.5 };

        var violations = GradingEnvelope.FindForPaths(FlatGround, new[] { RoadAtOne }, vertices, 1, 0.001);

        GradingEnvelopeViolation violation = Assert.Single(violations);
        Assert.Equal(0.0, violation.LowZ, 9);
        Assert.Equal(1.0, violation.HighZ, 9);
        Assert.Equal(1.5, violation.Overshoot, 9);
    }

    [Fact]
    public void FindForPaths_BatterPointBetweenDesignAndGround_IsNotViolation()
    {
        double[] vertices = { 5.0, 8.0, 0.4 };

        Assert.Empty(GradingEnvelope.FindForPaths(FlatGround, new[] { RoadAtOne }, vertices, 1, 0.001));
    }

    [Fact]
    public void FindForPaths_PointOnRoad_IsNotChecked()
    {
        // Inside the 2 m road a point carries the road's own grade, whatever the ground.
        double[] vertices = { 5.0, 5.5, 1.0 };

        Assert.Empty(GradingEnvelope.FindForPaths(FlatGround, new[] { RoadAtOne }, vertices, 1, 0.001));
    }

    [Fact]
    public void FindForPaths_PointAtFootOfVerticalWallFace_UsesWholeGroundBand()
    {
        // A wall with no plan thickness: foot at 0 and top at 2 along x = 6. The foot point is 0 below the
        // top and would read as cut below ground if the ground were the top's height alone.
        var wallTerrain = new IndexedTriMesh(
            new[]
            {
                0.0, 0.0, 0.0, 6.0, 0.0, 0.0, 6.0, 10.0, 0.0, 0.0, 10.0, 0.0,
                6.0, 0.0, 2.0, 12.0, 0.0, 2.0, 12.0, 10.0, 2.0, 6.0, 10.0, 2.0
            },
            8,
            new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 1, 4, 7, 1, 7, 2 },
            6);
        var road = new PathGrader.PathDefinition(new[] { 6.0, 1.0, 6.0, 9.0 }, new[] { 2.0, 2.0 }, 2, 0.0);
        double[] vertices = { 6.0, 5.0, 0.0 };

        Assert.Empty(GradingEnvelope.FindForPaths(wallTerrain, new[] { road }, vertices, 1, 0.001));
    }

    [Fact]
    public void FindForPads_PointBelowGroundBesideFillPad_IsViolation()
    {
        var pad = new PadGrader.PadBoundary(new[] { 4.0, 4.0, 6.0, 4.0, 6.0, 6.0, 4.0, 6.0 }, 4, targetZ: 1.0);
        double[] vertices = { 8.0, 5.0, -0.5 };

        GradingEnvelopeViolation violation = Assert.Single(
            GradingEnvelope.FindForPads(FlatGround, new[] { pad }, vertices, 1, 0.001));
        Assert.Equal(0.5, violation.Overshoot, 9);
    }

    [Fact]
    public void Clamp_PullsPointIntoBandAndFlagsFault()
    {
        var result = new GradingResult(
            new[] { 5.0, 8.0, 2.5 }, 1, Array.Empty<int>(), 0, 0.0, 0.0, Array.Empty<double>(), 0);
        var violations = GradingEnvelope.FindForPaths(FlatGround, new[] { RoadAtOne }, result.Vertices, 1, 0.001);

        GradingResult clamped = GradingEnvelope.Clamp(result, violations, "grade_path", "Grade Path");

        Assert.Equal(1.0, clamped.Vertices[2], 9);
        Assert.True(clamped.HasEnvelopeFault);
        Assert.False(result.HasEnvelopeFault);
    }
}
