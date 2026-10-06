using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class RegradedConstraintTrimmerTests
{
    /// <summary>A 20 x 20 grid at z = 0, raised to z = 2 inside x, y in [8, 12].</summary>
    private static TerrainFaceGrid GradedTerrain()
    {
        const int n = 20;
        var v = new List<double>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
                v.AddRange(new[] { (double)i, j, i >= 8 && i <= 12 && j >= 8 && j <= 12 ? 2.0 : 0.0 });
        }

        var f = new List<int>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = (j * (n + 1)) + i;
                f.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 });
            }
        }

        return new TerrainFaceGrid(v.ToArray(), v.Count / 3, f.ToArray(), f.Count / 3);
    }

    [Fact]
    public void Trim_BreaklineAcrossAGradedArea_KeepsOnlyTheParts_StillOnTheGround()
    {
        var points = new List<double>();
        for (int i = 0; i <= 20; i++)
            points.AddRange(new[] { (double)i, 10.0, 0.0 });   // surveyed at the old ground, z = 0
        var line = new ConstraintPolyline(points.ToArray(), 21, IsClosed: false);

        List<ConstraintPolyline> trimmed = RegradedConstraintTrimmer.Trim(
            new[] { line }, GradedTerrain(), 0.01, out double removed, out int trimmedLines);

        Assert.Equal(1, trimmedLines);
        Assert.Equal(2, trimmed.Count);
        Assert.Equal(7.0, trimmed[0].Points[(trimmed[0].PointCount - 1) * 3], 9);   // west run ends at x = 7
        Assert.Equal(13.0, trimmed[1].Points[0], 9);                                   // east run starts at x = 13
        Assert.Equal(6.0, removed, 9);                                                 // x 7..13
    }

    [Fact]
    public void Trim_LineOnTheGradedSurface_IsKeptWhole()
    {
        var line = new ConstraintPolyline(new double[] { 9, 9, 2, 11, 9, 2, 11, 11, 2 }, 3, IsClosed: false);

        List<ConstraintPolyline> trimmed = RegradedConstraintTrimmer.Trim(
            new[] { line }, GradedTerrain(), 0.01, out double removed, out int trimmedLines);

        Assert.Same(line.Points, Assert.Single(trimmed).Points);
        Assert.Equal(0, trimmedLines);
        Assert.Equal(0.0, removed);
    }

    [Fact]
    public void Trim_ClosedRingCrossingTheGradedArea_BecomesOneOpenRunAroundTheGap()
    {
        // A ring around (10, 4), radius 7: its top dips into the raised area.
        var points = new List<double>();
        const int segments = 32;
        for (int s = 0; s < segments; s++)
        {
            double t = 2 * Math.PI * s / segments;
            points.AddRange(new[] { 10 + (7 * Math.Cos(t)), 4 + (7 * Math.Sin(t)), 0.0 });
        }

        var ring = new ConstraintPolyline(points.ToArray(), segments, IsClosed: true);
        List<ConstraintPolyline> trimmed = RegradedConstraintTrimmer.Trim(
            new[] { ring }, GradedTerrain(), 0.01, out _, out _);

        ConstraintPolyline run = Assert.Single(trimmed);
        Assert.False(run.IsClosed);
        Assert.True(run.PointCount < segments);
    }
}
